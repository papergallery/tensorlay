#!/usr/bin/env python3
"""TensorLay Relay — pairing + service discovery daemon for VPS."""

import hmac
import os
import stat
import json
import urllib.request
from pathlib import Path
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
import uvicorn

app = FastAPI(title="TensorLay Relay", version="1.0.0")

DATA_DIR = Path("/opt/tensorlay-relay")
PAIRING_CODE_FILE = DATA_DIR / "pairing_code"

# Service definitions (same as desktop app)
SERVICES = [
    {"id": "sd-forge",  "name": "SD Forge",     "port": 7860, "category": "image",  "health": "/"},
    {"id": "comfyui",   "name": "ComfyUI",      "port": 8188, "category": "image",  "health": "/"},
    {"id": "ollama",    "name": "Ollama",        "port": 11434,"category": "text",   "health": "/api/tags"},
    {"id": "alltalk",   "name": "AllTalk TTS",   "port": 7851, "category": "audio",  "health": "/"},
    {"id": "musicgen",  "name": "MusicGen",      "port": 7861, "category": "audio",  "health": "/"},
    {"id": "triposr",   "name": "TripoSR",       "port": 7862, "category": "3d",     "health": "/"},
]


class PairRequest(BaseModel):
    code: str
    ssh_public_key: str


class PairResponse(BaseModel):
    success: bool
    ssh_user: str = ""
    ssh_port: int = 22
    error: str = ""


@app.get("/health")
def health():
    return {"status": "ok", "version": "1.0.0"}


@app.post("/pair", response_model=PairResponse)
def pair(req: PairRequest):
    # Read stored code
    if not PAIRING_CODE_FILE.exists():
        raise HTTPException(status_code=403, detail="No pairing code available. Run: tensorlay-relay --new-code")

    stored_code = PAIRING_CODE_FILE.read_text().strip()
    if not hmac.compare_digest(req.code.strip().upper(), stored_code.upper()):
        raise HTTPException(status_code=403, detail="Invalid pairing code")

    # Validate public key format
    key = req.ssh_public_key.strip()
    if not (key.startswith("ssh-") or key.startswith("ecdsa-")):
        raise HTTPException(status_code=400, detail="Invalid SSH public key format")

    # Add to authorized_keys
    ssh_dir = Path.home() / ".ssh"
    ssh_dir.mkdir(mode=0o700, exist_ok=True)
    auth_keys = ssh_dir / "authorized_keys"

    # Check if key already exists
    existing = auth_keys.read_text() if auth_keys.exists() else ""
    if key not in existing:
        with open(auth_keys, "a") as f:
            f.write(f"\n{key}\n")
        auth_keys.chmod(0o600)

    # Delete pairing code (single use)
    PAIRING_CODE_FILE.unlink(missing_ok=True)

    ssh_user = os.environ.get("SUDO_USER", os.environ.get("USER", "root"))
    return PairResponse(success=True, ssh_user=ssh_user, ssh_port=22)


@app.get("/services")
def services():
    result = []
    for svc in SERVICES:
        status = "offline"
        try:
            url = f"http://127.0.0.1:{svc['port']}{svc['health']}"
            req = urllib.request.Request(url, method="GET")
            with urllib.request.urlopen(req, timeout=2) as resp:
                if resp.status < 500:
                    status = "online"
        except Exception:
            pass
        result.append({
            "id": svc["id"],
            "name": svc["name"],
            "port": svc["port"],
            "category": svc["category"],
            "status": status,
        })
    return {"services": result}


@app.post("/new-code")
def new_code():
    """Generate a new pairing code (for re-pairing)."""
    import secrets
    code = secrets.token_hex(4).upper()
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    PAIRING_CODE_FILE.write_text(code)
    return {"code": code}


if __name__ == "__main__":
    import sys
    if "--new-code" in sys.argv:
        import secrets
        code = secrets.token_hex(4).upper()
        DATA_DIR.mkdir(parents=True, exist_ok=True)
        PAIRING_CODE_FILE.write_text(code)
        print(f"New pairing code: {code}")
        sys.exit(0)

    uvicorn.run(app, host="0.0.0.0", port=8090)
