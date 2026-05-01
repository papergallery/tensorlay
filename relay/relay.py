#!/usr/bin/env python3
"""TensorLay Relay — pairing + service discovery daemon for VPS.

Runs as the unprivileged `tensorlay` user (see install.sh). Pairing installs
SSH public keys into /home/tensorlay/.ssh/authorized_keys with restricted
options so the client can only open remote port-forwards on the AI service
ports — no shell, no exec, no other forwards.
"""

import base64
import hashlib
import hmac
import json
import logging
import os
import secrets
import sys
import urllib.request
from pathlib import Path
from typing import List, Optional

from fastapi import Depends, FastAPI, Header, HTTPException, Request, status
from fastapi.responses import JSONResponse
from pydantic import BaseModel
from slowapi import Limiter, _rate_limit_exceeded_handler
from slowapi.errors import RateLimitExceeded
from slowapi.util import get_remote_address
import uvicorn

# ── Constants ─────────────────────────────────────────────────────────────

VERSION = "1.2.0"
DATA_DIR = Path("/opt/tensorlay-relay")
PAIRING_CODE_FILE = DATA_DIR / "pairing_code"
SERVICE_TOKEN_FILE = DATA_DIR / "service_token"

# Service definitions — kept in sync with desktop ServiceRegistry.cs.
# `port` is the remote-forward port we permit on authorized_keys.
SERVICES = [
    {"id": "sd-forge", "name": "SD Forge",    "port": 7860,  "category": "image", "health": "/"},
    {"id": "comfyui",  "name": "ComfyUI",     "port": 8188,  "category": "image", "health": "/"},
    {"id": "ollama",   "name": "Ollama",      "port": 11434, "category": "text",  "health": "/api/tags"},
    {"id": "alltalk",  "name": "AllTalk TTS", "port": 7851,  "category": "audio", "health": "/"},
    {"id": "musicgen", "name": "MusicGen",    "port": 7861,  "category": "audio", "health": "/"},
    {"id": "triposr",  "name": "TripoSR",     "port": 7862,  "category": "3d",    "health": "/"},
]

# authorized_keys options for the paired client. `restrict` disables every
# capability; we then re-enable only remote-port-forwards (permitlisten) for
# the specific 127.0.0.1:<port> bindings the desktop app needs. No shell, no
# exec, no agent/X11/pty.
PERMIT_OPTIONS = "restrict," + ",".join(
    f'permitlisten="127.0.0.1:{s["port"]}"' for s in SERVICES
)

# ── Logging ───────────────────────────────────────────────────────────────

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
)
log = logging.getLogger("tensorlay-relay")

# ── App + rate limiter ────────────────────────────────────────────────────

limiter = Limiter(key_func=get_remote_address)
app = FastAPI(title="TensorLay Relay", version=VERSION)
app.state.limiter = limiter
app.add_exception_handler(RateLimitExceeded, _rate_limit_exceeded_handler)

# ── Models ────────────────────────────────────────────────────────────────


class PairRequest(BaseModel):
    code: str
    ssh_public_key: str


class PairResponse(BaseModel):
    success: bool
    ssh_user: str = ""
    ssh_port: int = 22
    service_token: str = ""
    # SHA256:base64 fingerprints of every host key the SSH server publishes.
    # The client pins these on first pair (TOFU) and rejects the SSH session
    # if a future Connect sees a fingerprint that isn't in the pinned set.
    host_key_fingerprints: List[str] = []
    error: str = ""


# ── Helpers ───────────────────────────────────────────────────────────────


def _client_ip(request: Request) -> str:
    return get_remote_address(request)


def _consume_pairing_code(submitted: str) -> bool:
    """Atomically read the stored code, compare, and delete on match.

    Returns True if codes match (and the file was deleted), False otherwise.
    Concurrent /pair attempts cannot both succeed — only the rename-winner
    sees the file.
    """
    if not PAIRING_CODE_FILE.exists():
        return False

    # Move the file aside first; whichever process wins the rename owns the
    # code. Other concurrent calls find it already gone.
    claim = PAIRING_CODE_FILE.with_suffix(f".claim.{os.getpid()}.{secrets.token_hex(2)}")
    try:
        PAIRING_CODE_FILE.rename(claim)
    except FileNotFoundError:
        return False

    try:
        stored = claim.read_text().strip()
        return hmac.compare_digest(submitted.strip().upper(), stored.upper())
    finally:
        claim.unlink(missing_ok=True)


def _compute_host_key_fingerprints() -> List[str]:
    """Read /etc/ssh/ssh_host_*.pub and return SHA256 fingerprints in
    OpenSSH format (matches `ssh-keygen -lf` output: `SHA256:<base64>`).

    The client pins these so any later SSH connect that returns a different
    host key is rejected — closes the MITM window for paired clients on
    every connect after the initial /pair call.
    """
    candidates = [
        "/etc/ssh/ssh_host_ed25519_key.pub",
        "/etc/ssh/ssh_host_rsa_key.pub",
        "/etc/ssh/ssh_host_ecdsa_key.pub",
    ]
    fingerprints: List[str] = []
    for path in candidates:
        try:
            with open(path, "r") as f:
                line = f.read().strip()
        except OSError:
            continue
        parts = line.split()
        if len(parts) < 2:
            continue
        try:
            key_bytes = base64.b64decode(parts[1])
        except (ValueError, base64.binascii.Error):
            continue
        digest = hashlib.sha256(key_bytes).digest()
        fp = base64.b64encode(digest).decode("ascii").rstrip("=")
        fingerprints.append(f"SHA256:{fp}")
    return fingerprints


def _validate_public_key(key: str) -> bool:
    """Surface-level format check for an OpenSSH public key.

    The desktop client generates ed25519 (preferred) or RSA via fallback.
    Accept the common SSH key types; reject anything else.
    """
    key = key.strip()
    if not key:
        return False
    parts = key.split(None, 2)
    if len(parts) < 2:
        return False
    keytype = parts[0]
    if keytype not in ("ssh-ed25519", "ssh-rsa", "ecdsa-sha2-nistp256",
                       "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521"):
        return False
    # Reject any whitespace or control character that could smuggle a
    # second authorized_keys entry past the format check. sshd splits on
    # newlines, but historical edge cases around tabs/null bytes in the
    # key options field exist — be conservative.
    if any(c in key for c in "\n\r\t\x00\x0b\x0c"):
        return False
    # Validate the base64 blob actually decodes to a plausible key length.
    # ed25519 wire-format is 51 bytes; RSA-2048 is ~270; ECDSA P-256 is ~104.
    # 50 bytes minimum filters out garbage like "AAAA" while accepting all
    # legitimate keys. validate=True makes b64decode reject extra chars.
    try:
        decoded = base64.b64decode(parts[1], validate=True)
    except (ValueError, base64.binascii.Error):
        return False
    if len(decoded) < 50:
        return False
    return True


def _append_authorized_key(key: str) -> None:
    """Append a key to authorized_keys with restricted options.

    Idempotent — a key already present (regardless of leading options) is
    not duplicated.
    """
    ssh_dir = Path.home() / ".ssh"
    ssh_dir.mkdir(mode=0o700, exist_ok=True)
    auth_keys = ssh_dir / "authorized_keys"

    existing = auth_keys.read_text() if auth_keys.exists() else ""
    if key in existing:
        log.info("public key already authorized; skipping append")
        return

    line = f"{PERMIT_OPTIONS} {key}\n"
    with auth_keys.open("a") as f:
        f.write(line)
    auth_keys.chmod(0o600)


def _issue_service_token() -> str:
    """Generate (or reuse) the long-lived bearer token for /services.

    Persisted so subsequent pairings on the same VPS share the same token —
    the desktop app stores it once and re-uses across reconnects. Stored
    file is mode 0600 in DATA_DIR (which the install script chowns to the
    relay user only).
    """
    if SERVICE_TOKEN_FILE.exists():
        return SERVICE_TOKEN_FILE.read_text().strip()
    token = secrets.token_urlsafe(32)
    SERVICE_TOKEN_FILE.write_text(token)
    SERVICE_TOKEN_FILE.chmod(0o600)
    return token


def _require_service_token(authorization: Optional[str] = Header(default=None)) -> None:
    """FastAPI dependency: bearer-token auth for /services."""
    if not SERVICE_TOKEN_FILE.exists():
        raise HTTPException(status_code=503, detail="Relay not paired yet")
    expected = SERVICE_TOKEN_FILE.read_text().strip()
    if not authorization or not authorization.lower().startswith("bearer "):
        raise HTTPException(status_code=401, detail="Missing bearer token")
    submitted = authorization.split(None, 1)[1].strip()
    if not hmac.compare_digest(submitted, expected):
        raise HTTPException(status_code=403, detail="Invalid token")


# ── Endpoints ─────────────────────────────────────────────────────────────


@app.get("/health")
def health():
    """Cheap unauthenticated liveness probe used by the desktop app to
    confirm the relay is reachable before submitting a pairing code."""
    return {"status": "ok", "version": VERSION}


@app.post("/pair", response_model=PairResponse)
@limiter.limit("5/minute")
def pair(request: Request, req: PairRequest):
    """Single-use pairing: validate code, install SSH key with restricted
    options, return service token for subsequent /services queries."""
    ip = _client_ip(request)

    if not _consume_pairing_code(req.code):
        log.warning("pair: invalid or missing code from %s", ip)
        raise HTTPException(status_code=403, detail="Invalid or expired pairing code")

    if not _validate_public_key(req.ssh_public_key):
        log.warning("pair: invalid public key format from %s", ip)
        raise HTTPException(status_code=400, detail="Invalid SSH public key format")

    try:
        _append_authorized_key(req.ssh_public_key.strip())
    except Exception as ex:
        log.exception("pair: failed to install key from %s: %s", ip, ex)
        raise HTTPException(status_code=500, detail="Failed to install SSH key")

    token = _issue_service_token()
    ssh_user = os.environ.get("USER") or "tensorlay"
    fingerprints = _compute_host_key_fingerprints()
    log.info("pair: success for %s (user=%s, fingerprints=%d)", ip, ssh_user, len(fingerprints))

    return PairResponse(
        success=True,
        ssh_user=ssh_user,
        ssh_port=22,
        service_token=token,
        host_key_fingerprints=fingerprints,
    )


@app.get("/services")
def services(_: None = Depends(_require_service_token)):
    """List AI services with health status (paired clients only)."""
    result = []
    for svc in SERVICES:
        status_str = "offline"
        try:
            url = f"http://127.0.0.1:{svc['port']}{svc['health']}"
            with urllib.request.urlopen(url, timeout=2) as resp:
                if resp.status < 500:
                    status_str = "online"
        except Exception:
            pass
        result.append({
            "id": svc["id"],
            "name": svc["name"],
            "port": svc["port"],
            "category": svc["category"],
            "status": status_str,
        })
    return {"services": result}


# ── CLI ───────────────────────────────────────────────────────────────────


def _cli_new_code() -> int:
    """Generate and store a new single-use pairing code. Print to stdout."""
    code = secrets.token_hex(4).upper()
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    PAIRING_CODE_FILE.write_text(code)
    try:
        PAIRING_CODE_FILE.chmod(0o600)
    except PermissionError:
        # Best-effort — owner of DATA_DIR may differ from the invoker.
        pass
    print(f"New pairing code: {code}")
    return 0


def main() -> int:
    if "--new-code" in sys.argv:
        return _cli_new_code()
    log.info("starting tensorlay-relay v%s on 0.0.0.0:8090", VERSION)
    uvicorn.run(app, host="0.0.0.0", port=8090, log_level="info")
    return 0


if __name__ == "__main__":
    sys.exit(main())
