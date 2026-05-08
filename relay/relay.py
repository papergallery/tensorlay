#!/usr/bin/env python3
"""TensorLay Relay — pairing + service discovery daemon for VPS.

Runs as the unprivileged `tensorlay` user (see install.sh). Pairing installs
SSH public keys into /home/tensorlay/.ssh/authorized_keys with restricted
options so the client can only open remote port-forwards on the AI service
ports — no shell, no exec, no other forwards.
"""

import asyncio
import base64
import contextlib
import hashlib
import hmac
import json
import logging
import os
import re
import secrets
import sqlite3
import sys
import time
import urllib.parse
import urllib.request
import uuid
from pathlib import Path
from typing import List, Optional

from fastapi import Depends, FastAPI, Header, HTTPException, Request, status
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field
from slowapi import Limiter, _rate_limit_exceeded_handler
from slowapi.errors import RateLimitExceeded
from slowapi.util import get_remote_address
import uvicorn

try:
    import yaml  # type: ignore
except ImportError:
    yaml = None  # config.yaml then optional; defaults are still applied

# ── Constants ─────────────────────────────────────────────────────────────

VERSION = "1.3.6"
DATA_DIR = Path("/opt/tensorlay-relay")
PAIRING_CODE_FILE = DATA_DIR / "pairing_code"
SERVICE_TOKEN_FILE = DATA_DIR / "service_token"

# v1.3.0 — remote install request feature
TASKS_DB_FILE = DATA_DIR / "tasks.db"
AGENT_TOKEN_FILE = DATA_DIR / "agent_token"            # set by --issue-agent-token
REMOTE_TASKS_TOKEN_FILE = DATA_DIR / "remote_tasks_token"  # rotated on every /pair
CONFIG_FILE = DATA_DIR / "config.yaml"

DEFAULT_TASK_TTL_SECONDS = 86400          # 24h before pending tasks auto-expire
DEFAULT_TERMINAL_RETENTION_SECONDS = 7 * 86400  # keep finished tasks for 7d as audit trail
DEFAULT_EXPIRE_INTERVAL_SECONDS = 300      # background sweep cadence
DEFAULT_AGENT_LABEL = "VPS agent"
DEFAULT_INSTALL_RATE_LIMIT = "10/hour"     # SlowAPI string for /api/tasks/install-model
# Reaper-failure suppression. The desktop agent at startup posts
# state="failed" with reason like "Desktop restarted before completion" for
# any task it found mid-download. That's not a real failure — the task was
# just running when the agent restarted. Without suppression the user has
# to re-approve from scratch (and lose the partial download). Bug observed
# 2026-05-05: 3 of 5 model installs were killed inside a 10-second window.
# When this pattern matches, the relay reverts the task to 'approved' so
# the agent re-pulls it on the next poll without another GUI click.
REAPER_FAILURE_PATTERN = re.compile(r"restart", re.IGNORECASE)

DEFAULT_ALLOWLIST_HOSTS = [
    "huggingface.co",
    "hf.co",
    "civitai.com",
    "github.com",
    "objects.githubusercontent.com",
    "cdn-lfs.huggingface.co",
    "cdn-lfs-us-1.huggingface.co",
]

VALID_TASK_STATES = {
    "pending", "approved", "downloading",
    "completed", "failed", "cancelled", "rejected", "expired", "revoked",
}
TERMINAL_STATES = {"completed", "failed", "cancelled", "rejected", "expired", "revoked"}
# Desktop-driven transitions enforced by POST /api/tasks/{id}/status. Anything
# not in this map is rejected with 400 — keeps the state machine honest and
# stops a buggy desktop from flipping a completed task back to pending.
DESKTOP_TRANSITIONS = {
    "pending":     {"approved", "rejected"},
    "approved":    {"downloading", "failed", "cancelled"},
    "downloading": {"completed", "failed", "cancelled"},
}

# Service definitions — kept in sync with desktop ServiceRegistry.cs.
# `port` is the remote-forward port we permit on authorized_keys.
# alltalk/musicgen/triposr returned in v0.9.11 once the desktop registry
# regained env-var + post-install support — see ServiceRegistry.cs for the
# upstream brittleness notes per service. KEEP this list aligned with the
# desktop registry; drift means /pair authorizes the wrong ports and the
# `permitlisten` whitelist below either rejects legitimate forwards or
# allows extras.
SERVICES = [
    {"id": "sd-forge", "name": "SD Forge",    "port": 7860,  "category": "image", "health": "/"},
    {"id": "comfyui",  "name": "ComfyUI",     "port": 8188,  "category": "image", "health": "/"},
    {"id": "ollama",   "name": "Ollama",      "port": 11434, "category": "text",  "health": "/api/tags"},
    {"id": "alltalk",  "name": "AllTalk TTS", "port": 7851,  "category": "audio", "health": "/api/ready"},
    {"id": "musicgen", "name": "MusicGen",    "port": 7861,  "category": "audio", "health": "/"},
    {"id": "triposr",  "name": "TripoSR",     "port": 7862,  "category": "3d",    "health": "/"},
]

# authorized_keys options for the paired client. We list the disable flags
# explicitly (instead of `restrict`) because OpenSSH 9.6 on Ubuntu does not
# let `permitlisten` override the `no-port-forwarding` flag that `restrict`
# implies — sshd refuses with "Server has disabled port forwarding" before
# pattern matching runs. Skipping `no-port-forwarding` and relying on the
# permitlisten white-list (which alone restricts -R to the listed ports)
# achieves the same security posture: no shell, no exec, no agent/X11/pty,
# and remote-forward only on the AI service ports.
PERMIT_OPTIONS = "no-agent-forwarding,no-pty,no-user-rc,no-X11-forwarding," + ",".join(
    f'permitlisten="127.0.0.1:{s["port"]}"' for s in SERVICES
)

# ── Logging ───────────────────────────────────────────────────────────────

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
)
log = logging.getLogger("tensorlay-relay")

# ── Config (loaded once at import time) ───────────────────────────────────


def _load_config() -> dict:
    """Read /opt/tensorlay-relay/config.yaml if present, fall back to
    compiled-in defaults otherwise. No hot-reload — restart the systemd unit
    to pick up changes. Defaults are intentionally generous (24h TTL, broad
    hf.co/civitai/github allowlist) so a fresh install works without
    needing to write a config file at all.
    """
    cfg = {
        "tasks": {
            "ttl_seconds": DEFAULT_TASK_TTL_SECONDS,
            "expire_check_interval": DEFAULT_EXPIRE_INTERVAL_SECONDS,
            "terminal_retention_seconds": DEFAULT_TERMINAL_RETENTION_SECONDS,
            "install_rate_limit": DEFAULT_INSTALL_RATE_LIMIT,
        },
        "allowlist": {"hosts": list(DEFAULT_ALLOWLIST_HOSTS)},
        "agent": {"label": DEFAULT_AGENT_LABEL},
    }
    if not CONFIG_FILE.exists():
        return cfg
    if yaml is None:
        log.warning("config.yaml present but PyYAML not installed; using defaults")
        return cfg
    try:
        with CONFIG_FILE.open("r") as f:
            user = yaml.safe_load(f) or {}
        # Shallow merge — top-level keys overwritten if present.
        if isinstance(user.get("tasks"), dict):
            cfg["tasks"].update(user["tasks"])
        if isinstance(user.get("allowlist"), dict) and isinstance(user["allowlist"].get("hosts"), list):
            cfg["allowlist"]["hosts"] = [str(h).lower() for h in user["allowlist"]["hosts"]]
        if isinstance(user.get("agent"), dict) and isinstance(user["agent"].get("label"), str):
            cfg["agent"]["label"] = user["agent"]["label"]
    except Exception as ex:
        log.warning("failed to parse config.yaml (%s); using defaults", ex)
    return cfg


CONFIG = _load_config()

# ── App + rate limiter ────────────────────────────────────────────────────


def _agent_key(request: Request) -> str:
    """Rate-limit key for agent-facing endpoints — derived from the bearer
    token, not the source IP. The agent runs on the same VPS as the relay
    (loopback) so per-IP keying would treat every agent as the same caller
    and let one agent's burst exhaust the others' quota.
    """
    auth = request.headers.get("authorization", "")
    if auth.lower().startswith("bearer "):
        return "agent:" + auth.split(None, 1)[1].strip()[:16]
    return get_remote_address(request)


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
    # v1.3.0 — bearer for /api/tasks/* (remote install requests). Always rotated
    # on each /pair so re-pairing replaces an old desktop's authority. Empty on
    # legacy clients that don't expect this field — they ignore it.
    remote_tasks_token: str = ""
    error: str = ""


class InstallModelRequest(BaseModel):
    service_id: str = Field(..., min_length=1, max_length=64)
    url: str = Field(..., min_length=1, max_length=2048)
    display_name: str = Field(..., min_length=1, max_length=200)
    size_mb: Optional[float] = None
    sha256: Optional[str] = Field(default=None, max_length=128)
    reason: Optional[str] = Field(default=None, max_length=500)
    # v1.3.3 — optional ComfyUI models subfolder ('loras', 'vae', 'pulid',
    # 'insightface/models/antelopev2', etc). When None the desktop falls back
    # to its legacy default of models/checkpoints/. Strict validation lives
    # in the install-model endpoint (see _validate_target_subdir).
    target_subdir: Optional[str] = Field(default=None, max_length=128)


class TaskStatusUpdate(BaseModel):
    state: str
    progress_pct: Optional[float] = None
    error_msg: Optional[str] = Field(default=None, max_length=500)


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


# ── Task auth + storage (v1.3.0) ───────────────────────────────────────────


def _issue_remote_tasks_token() -> str:
    """Generate a fresh bearer token for /api/tasks/*. Always rotated on every
    /pair so re-pairing invalidates the previous desktop's authority — single-
    desktop model, last-pair-wins. Multi-desktop support would require a
    per-key token table; deferred past v0.9.0.
    """
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    token = secrets.token_urlsafe(32)
    REMOTE_TASKS_TOKEN_FILE.write_text(token)
    REMOTE_TASKS_TOKEN_FILE.chmod(0o600)
    return token


def _require_remote_tasks_token(authorization: Optional[str] = Header(default=None)) -> None:
    """FastAPI dependency: desktop-side bearer for /api/tasks/* (poll, status)."""
    if not REMOTE_TASKS_TOKEN_FILE.exists():
        raise HTTPException(status_code=503, detail="Relay not paired yet")
    expected = REMOTE_TASKS_TOKEN_FILE.read_text().strip()
    if not authorization or not authorization.lower().startswith("bearer "):
        raise HTTPException(status_code=401, detail="Missing bearer token")
    submitted = authorization.split(None, 1)[1].strip()
    if not hmac.compare_digest(submitted, expected):
        raise HTTPException(status_code=403, detail="Invalid token")


def _require_agent_token(authorization: Optional[str] = Header(default=None)) -> None:
    """FastAPI dependency: agent-side bearer for /api/tasks/install-model
    and /api/tasks/{id} GET/DELETE. Issued once via `--issue-agent-token`
    CLI; rotated by re-running the same command (old token instantly invalid)."""
    if not AGENT_TOKEN_FILE.exists():
        raise HTTPException(status_code=503, detail="No agent token issued yet")
    expected = AGENT_TOKEN_FILE.read_text().strip()
    if not authorization or not authorization.lower().startswith("bearer "):
        raise HTTPException(status_code=401, detail="Missing bearer token")
    submitted = authorization.split(None, 1)[1].strip()
    if not hmac.compare_digest(submitted, expected):
        raise HTTPException(status_code=403, detail="Invalid agent token")


def _is_url_allowed(url: str) -> bool:
    """Hostname-suffix allowlist + https-only. Suffix match guards against
    `evil-huggingface.co` masquerading as `huggingface.co`. The desktop's
    HttpClient follows redirects at download time, so an allowlisted URL
    that 302s to an unlisted CDN still proceeds — SHA-256 in the manifest
    (when supplied) is the integrity backstop for that case.
    """
    try:
        parsed = urllib.parse.urlparse(url)
    except Exception:
        return False
    if parsed.scheme != "https":
        return False
    host = (parsed.netloc or "").lower().split(":")[0]
    if not host:
        return False
    allow = CONFIG["allowlist"]["hosts"]
    return any(host == h or host.endswith("." + h) for h in allow)


def _init_db() -> None:
    """Create tasks table on first run. WAL mode lets the FastAPI worker
    process and the background expire loop both read/write without blocking
    each other on a single-writer load."""
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    with sqlite3.connect(str(TASKS_DB_FILE)) as conn:
        conn.execute("PRAGMA journal_mode=WAL")
        conn.execute("PRAGMA busy_timeout=5000")
        conn.execute("""
            CREATE TABLE IF NOT EXISTS tasks (
                id           TEXT PRIMARY KEY,
                created_at   REAL NOT NULL,
                updated_at   REAL NOT NULL,
                state        TEXT NOT NULL DEFAULT 'pending',
                service_id   TEXT NOT NULL,
                url          TEXT NOT NULL,
                display_name TEXT NOT NULL,
                size_mb      REAL,
                sha256       TEXT,
                reason       TEXT,
                agent_label  TEXT NOT NULL,
                expires_at   REAL NOT NULL,
                progress_pct REAL,
                error_msg    TEXT
            )
        """)
        conn.execute("CREATE INDEX IF NOT EXISTS tasks_state_idx ON tasks(state)")
        conn.execute("CREATE INDEX IF NOT EXISTS tasks_expires_idx ON tasks(expires_at)")
        # v1.3.3 — in-place migration for the optional ComfyUI subfolder field.
        # SQLite has no IF NOT EXISTS for ADD COLUMN, so we swallow the
        # "duplicate column name" OperationalError on already-migrated DBs.
        try:
            conn.execute("ALTER TABLE tasks ADD COLUMN target_subdir TEXT")
        except sqlite3.OperationalError:
            pass
        conn.commit()


@contextlib.contextmanager
def _db():
    """Per-call connection. WAL + busy_timeout in _init_db handles concurrency;
    keeping connection lifetime short avoids 'database is locked' across the
    occasional long-running request."""
    conn = sqlite3.connect(str(TASKS_DB_FILE))
    conn.row_factory = sqlite3.Row
    try:
        yield conn
        conn.commit()
    except Exception:
        conn.rollback()
        raise
    finally:
        conn.close()


def _row_to_task(row: sqlite3.Row, *, include_internal: bool = False) -> dict:
    """Project a DB row to the JSON shape the desktop expects. Timestamps
    are emitted as ISO-8601 UTC strings — easier to parse from C# than raw
    floats and consistent with the rest of the API."""
    out = {
        "id": row["id"],
        "service_id": row["service_id"],
        "url": row["url"],
        "display_name": row["display_name"],
        "size_mb": row["size_mb"],
        "sha256": row["sha256"],
        "reason": row["reason"],
        "agent_label": row["agent_label"],
        "state": row["state"],
        "created_at": _iso(row["created_at"]),
        "expires_at": _iso(row["expires_at"]),
        # v1.3.3 — guarded read because rows inserted before the ALTER won't
        # have the column key in the Row mapping on some sqlite3 builds.
        "target_subdir": row["target_subdir"] if "target_subdir" in row.keys() else None,
    }
    if include_internal:
        out["progress_pct"] = row["progress_pct"]
        out["error_msg"] = row["error_msg"]
        out["updated_at"] = _iso(row["updated_at"])
    return out


def _iso(ts: float) -> str:
    """Unix timestamp → ISO-8601 UTC, no microseconds, with trailing Z."""
    import datetime as _dt
    return _dt.datetime.utcfromtimestamp(float(ts)).replace(microsecond=0).isoformat() + "Z"


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
    # Always rotate remote_tasks_token on /pair: the previous desktop's
    # authority is revoked the moment a new one pairs. Single-desktop model
    # for v0.9.0 — multi-desktop would need a per-key token mapping.
    tasks_token = _issue_remote_tasks_token()
    ssh_user = os.environ.get("USER") or "tensorlay"
    fingerprints = _compute_host_key_fingerprints()
    log.info("pair: success for %s (user=%s, fingerprints=%d)", ip, ssh_user, len(fingerprints))

    return PairResponse(
        success=True,
        ssh_user=ssh_user,
        ssh_port=22,
        service_token=token,
        host_key_fingerprints=fingerprints,
        remote_tasks_token=tasks_token,
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


# ── Task endpoints (v1.3.0 — remote install requests) ─────────────────────


@app.post("/api/tasks/install-model", status_code=202)
@limiter.limit(CONFIG["tasks"]["install_rate_limit"], key_func=_agent_key)
def task_install_model(
    request: Request,
    body: InstallModelRequest,
    _: None = Depends(_require_agent_token),
):
    """Submit a model-install request from a trusted external agent. The
    desktop (paired and with AllowRemoteInstallRequests=ON) will surface
    this in an approval modal; nothing is downloaded without explicit user
    consent. Validation here is the safety floor — bad service_id or a URL
    outside the allowlist is rejected before the row is created.
    """
    service_ids = {s["id"] for s in SERVICES}
    if body.service_id not in service_ids:
        raise HTTPException(status_code=400, detail=f"Unknown service_id: {body.service_id}")
    if not _is_url_allowed(body.url):
        host = (urllib.parse.urlparse(body.url).netloc or "").lower().split(":")[0]
        log.warning("install-model: rejected disallowed URL host '%s'", host)
        raise HTTPException(status_code=400, detail=f"URL host not in allowlist: {host or '(empty)'}")
    if body.size_mb is not None and body.size_mb <= 0:
        raise HTTPException(status_code=400, detail="size_mb must be positive")

    target_subdir: Optional[str] = None
    if body.target_subdir is not None:
        candidate = body.target_subdir.strip()
        # Strict allowlist — this string is concatenated into a filesystem
        # path on the desktop side, so we reject anything that could escape
        # <comfy_root>/models/ or do platform-specific weirdness (drive
        # letters, backslashes, NUL). Empty after strip is also a reject.
        bad = False
        reason = ""
        if not candidate:
            bad, reason = True, "empty"
        elif candidate.startswith("/") or candidate.endswith("/"):
            bad, reason = True, "leading or trailing slash"
        elif not re.fullmatch(r"[A-Za-z0-9_/-]+", candidate):
            bad, reason = True, "characters outside [A-Za-z0-9_/-]"
        else:
            segments = candidate.split("/")
            if len(segments) > 4:
                bad, reason = True, "more than 4 path segments"
            else:
                for seg in segments:
                    if not seg or seg == ".." or seg.startswith("."):
                        bad, reason = True, f"bad segment {seg!r}"
                        break
        if bad:
            log.warning("install-model: rejected target_subdir=%r (%s)", body.target_subdir, reason)
            raise HTTPException(status_code=400, detail=f"Invalid target_subdir: {reason}")
        target_subdir = candidate

    task_id = str(uuid.uuid4())
    now = time.time()
    expires = now + float(CONFIG["tasks"]["ttl_seconds"])
    with _db() as conn:
        conn.execute(
            "INSERT INTO tasks (id, created_at, updated_at, state, service_id, url, "
            "display_name, size_mb, sha256, reason, agent_label, expires_at, target_subdir) "
            "VALUES (?, ?, ?, 'pending', ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            (
                task_id, now, now,
                body.service_id, body.url, body.display_name,
                body.size_mb, body.sha256, body.reason,
                CONFIG["agent"]["label"], expires, target_subdir,
            ),
        )
    log.info("install-model: created task=%s service=%s name=%r", task_id, body.service_id, body.display_name)
    return {"task_id": task_id}


@app.get("/api/tasks/pending")
def task_pending(_: None = Depends(_require_remote_tasks_token)):
    """Desktop polls this. Returns ALL non-terminal tasks belonging to this
    paired client — pending awaiting decision, plus approved/downloading
    entries that may have been left behind by a previous app session
    crash/restart so the desktop can clean them up. Terminal-state tasks
    (completed/failed/etc.) are not returned.
    """
    now = time.time()
    with _db() as conn:
        rows = conn.execute(
            "SELECT * FROM tasks "
            "WHERE state IN ('pending', 'approved', 'downloading') "
            "AND expires_at > ? "
            "ORDER BY created_at ASC",
            (now,),
        ).fetchall()
    return {"tasks": [_row_to_task(r) for r in rows]}


@app.post("/api/tasks/{task_id}/status")
def task_post_status(
    task_id: str,
    body: TaskStatusUpdate,
    _: None = Depends(_require_remote_tasks_token),
):
    """Desktop reports lifecycle progress: approved → downloading (with
    progress_pct) → completed | failed | cancelled, OR pending → rejected.
    Illegal transitions are 400'd — the relay state machine is authoritative.
    """
    if body.state not in VALID_TASK_STATES:
        raise HTTPException(status_code=400, detail=f"Invalid state: {body.state}")
    if body.progress_pct is not None and not (0.0 <= body.progress_pct <= 100.0):
        raise HTTPException(status_code=400, detail="progress_pct must be 0..100")
    now = time.time()
    with _db() as conn:
        row = conn.execute("SELECT state FROM tasks WHERE id = ?", (task_id,)).fetchone()
        if row is None:
            raise HTTPException(status_code=404, detail="Task not found")
        current = row[0]
        if current in TERMINAL_STATES:
            raise HTTPException(status_code=409, detail=f"Task already {current}")
        # Same-state progress update — accept as no-op state change so older
        # desktop clients (pre-0.9.7) that re-send state="downloading" on
        # every progress event don't earn a 400 storm. Only progress_pct is
        # updated; state stays as-is. Defense in depth — modern clients
        # already dedup client-side.
        if body.state == current == "downloading":
            conn.execute(
                "UPDATE tasks SET progress_pct = ?, updated_at = ? WHERE id = ?",
                (body.progress_pct, now, task_id),
            )
            return {"ok": True}
        # Reaper-failure suppression — see REAPER_FAILURE_PATTERN docstring.
        if (
            current == "downloading"
            and body.state == "failed"
            and body.error_msg
            and REAPER_FAILURE_PATTERN.search(body.error_msg)
        ):
            log.warning(
                "reaper-failure suppressed for task %s (reason=%r) — reverting to approved",
                task_id, body.error_msg,
            )
            conn.execute(
                "UPDATE tasks SET state = 'approved', error_msg = NULL, "
                "progress_pct = NULL, updated_at = ? WHERE id = ?",
                (now, task_id),
            )
            return {"ok": True, "suppressed": "reaper-failure"}
        allowed_next = DESKTOP_TRANSITIONS.get(current, set())
        if body.state not in allowed_next:
            raise HTTPException(
                status_code=400,
                detail=f"Illegal transition {current} -> {body.state}",
            )
        conn.execute(
            "UPDATE tasks SET state = ?, progress_pct = ?, error_msg = ?, updated_at = ? "
            "WHERE id = ?",
            (body.state, body.progress_pct, body.error_msg, now, task_id),
        )
    return {"ok": True}


@app.get("/api/tasks/{task_id}")
def task_get(task_id: str, _: None = Depends(_require_agent_token)):
    """Agent polls this to see whether the user approved/rejected, and to
    watch download progress without holding the desktop on the line."""
    with _db() as conn:
        row = conn.execute("SELECT * FROM tasks WHERE id = ?", (task_id,)).fetchone()
    if row is None:
        raise HTTPException(status_code=404, detail="Task not found")
    return _row_to_task(row, include_internal=True)


@app.delete("/api/tasks/{task_id}")
def task_revoke(task_id: str, _: None = Depends(_require_agent_token)):
    """Agent revokes a task before the user has decided. After the user has
    approved, rejected, or the task has progressed, this is a no-op (409) —
    the agent can't unilaterally cancel a download in progress. Use case:
    agent realises it submitted the wrong URL and wants to retract."""
    now = time.time()
    with _db() as conn:
        row = conn.execute("SELECT state FROM tasks WHERE id = ?", (task_id,)).fetchone()
        if row is None:
            raise HTTPException(status_code=404, detail="Task not found")
        if row[0] != "pending":
            raise HTTPException(status_code=409, detail=f"Task not pending (state={row[0]})")
        conn.execute(
            "UPDATE tasks SET state = 'revoked', updated_at = ? WHERE id = ?",
            (now, task_id),
        )
    return {"ok": True}


# ── Background expire loop ────────────────────────────────────────────────


async def _expire_loop():
    """Sweep every 5 min: flip pending-past-TTL to expired, purge terminal
    rows older than 7d. Both bounds come from config.yaml."""
    interval = float(CONFIG["tasks"]["expire_check_interval"])
    retention = float(CONFIG["tasks"]["terminal_retention_seconds"])
    while True:
        try:
            await asyncio.sleep(interval)
            now = time.time()
            with _db() as conn:
                cur = conn.execute(
                    "UPDATE tasks SET state = 'expired', updated_at = ? "
                    "WHERE state = 'pending' AND expires_at < ?",
                    (now, now),
                )
                expired_count = cur.rowcount
                cur = conn.execute(
                    "DELETE FROM tasks "
                    "WHERE state IN ('completed','rejected','expired','revoked','failed','cancelled') "
                    "AND updated_at < ?",
                    (now - retention,),
                )
                purged_count = cur.rowcount
            if expired_count or purged_count:
                log.info("expire_loop: expired=%d purged=%d", expired_count, purged_count)
        except asyncio.CancelledError:
            raise
        except Exception:
            log.exception("expire_loop iteration failed")


@app.on_event("startup")
async def _startup():
    _init_db()
    asyncio.create_task(_expire_loop())
    log.info("v%s ready: ttl=%ss allowlist=%d hosts",
             VERSION, CONFIG["tasks"]["ttl_seconds"], len(CONFIG["allowlist"]["hosts"]))


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


def _cli_issue_agent_token() -> int:
    """Generate (or rotate) the agent token. Re-running this invalidates
    the old token — by design — so an admin who suspects compromise just
    runs the command again and updates the env var. Prints the token to
    stdout exactly once; never logged."""
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    token = secrets.token_urlsafe(32)
    AGENT_TOKEN_FILE.write_text(token)
    try:
        AGENT_TOKEN_FILE.chmod(0o600)
    except PermissionError:
        pass
    print(f"Agent token: {token}")
    print("Save this value in TENSORLAY_AGENT_TOKEN on the agent's environment.")
    print("Re-run this command to rotate.")
    return 0


def main() -> int:
    if "--new-code" in sys.argv:
        return _cli_new_code()
    if "--issue-agent-token" in sys.argv:
        return _cli_issue_agent_token()
    log.info("starting tensorlay-relay v%s on 0.0.0.0:8090", VERSION)
    uvicorn.run(app, host="0.0.0.0", port=8090, log_level="info")
    return 0


if __name__ == "__main__":
    sys.exit(main())
