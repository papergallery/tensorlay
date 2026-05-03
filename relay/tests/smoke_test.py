"""End-to-end smoke test for the v1.3.0 relay endpoints.

Spins up the relay against an isolated temp DATA_DIR, fakes a /pair to seed
remote_tasks_token, issues an agent token, then drives the full task
lifecycle through HTTP. Exits non-zero on any unexpected response.

Run: /tmp/relay_venv/bin/python /tmp/relay_smoke_test.py
"""
import asyncio
import os
import shutil
import sys
import tempfile
from pathlib import Path

import httpx

TEST_DIR = Path(tempfile.mkdtemp(prefix="relay-smoke-"))
sys.path.insert(0, "/var/www/html/Other/tensorlay/relay")
import relay  # noqa: E402

relay.DATA_DIR = TEST_DIR
relay.PAIRING_CODE_FILE = TEST_DIR / "pairing_code"
relay.SERVICE_TOKEN_FILE = TEST_DIR / "service_token"
relay.TASKS_DB_FILE = TEST_DIR / "tasks.db"
relay.AGENT_TOKEN_FILE = TEST_DIR / "agent_token"
relay.REMOTE_TASKS_TOKEN_FILE = TEST_DIR / "remote_tasks_token"
relay.CONFIG_FILE = TEST_DIR / "config.yaml"
relay._init_db()

failures: list[str] = []


def check(label: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {label}")
    else:
        print(f"  FAIL {label}: {detail}")
        failures.append(label)


async def main() -> None:
    transport = httpx.ASGITransport(app=relay.app)
    async with httpx.AsyncClient(transport=transport, base_url="http://test") as c:
        r = await c.get("/health")
        check("health 200", r.status_code == 200 and r.json()["version"] == "1.3.0")

        r = await c.get("/api/tasks/pending")
        check("pending unauthed → 503 (no token file)", r.status_code == 503)

        r = await c.post(
            "/api/tasks/install-model",
            json={"service_id": "sd-forge", "url": "x", "display_name": "x"},
        )
        check("install-model unauthed → 503", r.status_code == 503)

        relay._cli_issue_agent_token()
        agent_token = relay.AGENT_TOKEN_FILE.read_text().strip()
        check("agent_token file exists", relay.AGENT_TOKEN_FILE.exists())
        check("agent_token length sane", len(agent_token) >= 30)

        ahdr = {"Authorization": f"Bearer {agent_token}"}

        r = await c.post(
            "/api/tasks/install-model",
            json={"service_id": "sd-forge", "url": "https://huggingface.co/x.safetensors", "display_name": "x"},
            headers={"Authorization": "Bearer wrong"},
        )
        check("install-model bad token → 403", r.status_code == 403)

        r = await c.post(
            "/api/tasks/install-model",
            json={"service_id": "sd-forge", "url": "https://evil.example.com/x.safetensors", "display_name": "x"},
            headers=ahdr,
        )
        check("install-model disallowed host → 400", r.status_code == 400, r.text)

        r = await c.post(
            "/api/tasks/install-model",
            json={"service_id": "sd-forge", "url": "http://huggingface.co/x.safetensors", "display_name": "x"},
            headers=ahdr,
        )
        check("install-model http scheme → 400", r.status_code == 400, r.text)

        r = await c.post(
            "/api/tasks/install-model",
            json={"service_id": "nonexistent", "url": "https://huggingface.co/x.safetensors", "display_name": "x"},
            headers=ahdr,
        )
        check("install-model bad service_id → 400", r.status_code == 400, r.text)

        r = await c.post(
            "/api/tasks/install-model",
            json={
                "service_id": "sd-forge",
                "url": "https://huggingface.co/RunDiffusion/Juggernaut-XL-v9/resolve/main/Juggernaut-XL_v9.safetensors",
                "display_name": "Juggernaut XL v9",
                "size_mb": 6800.0,
                "reason": "smoke test",
            },
            headers=ahdr,
        )
        check("install-model happy path → 202", r.status_code == 202, r.text)
        task_id = r.json().get("task_id", "")
        check("task_id is uuid-shaped", len(task_id) == 36)

        r = await c.get("/api/tasks/pending")
        check("pending without paired token → 503", r.status_code == 503)

        tasks_token = relay._issue_remote_tasks_token()
        check("remote_tasks_token issued", relay.REMOTE_TASKS_TOKEN_FILE.exists())
        thdr = {"Authorization": f"Bearer {tasks_token}"}

        r = await c.get("/api/tasks/pending", headers=thdr)
        check("pending with token → 200", r.status_code == 200, r.text)
        pending = r.json()["tasks"]
        check("pending list has 1 entry", len(pending) == 1)
        check("pending task_id matches", pending[0]["id"] == task_id)
        check("pending shows allowlisted URL", "huggingface.co" in pending[0]["url"])
        check("pending state=pending", pending[0]["state"] == "pending")

        r = await c.get(f"/api/tasks/{task_id}", headers=ahdr)
        check("agent GET task → 200", r.status_code == 200)
        check("agent sees state=pending", r.json()["state"] == "pending")

        r = await c.post(
            f"/api/tasks/{task_id}/status",
            json={"state": "approved"},
            headers=thdr,
        )
        check("status pending→approved → 200", r.status_code == 200, r.text)

        r = await c.post(
            f"/api/tasks/{task_id}/status",
            json={"state": "pending"},
            headers=thdr,
        )
        check("status approved→pending illegal → 400", r.status_code == 400, r.text)

        r = await c.post(
            f"/api/tasks/{task_id}/status",
            json={"state": "downloading", "progress_pct": 42.5},
            headers=thdr,
        )
        check("status approved→downloading → 200", r.status_code == 200, r.text)

        r = await c.post(
            f"/api/tasks/{task_id}/status",
            json={"state": "downloading", "progress_pct": 150.0},
            headers=thdr,
        )
        check("status progress >100 → 400", r.status_code == 400, r.text)

        r = await c.post(
            f"/api/tasks/{task_id}/status",
            json={"state": "completed", "progress_pct": 100.0},
            headers=thdr,
        )
        check("status downloading→completed → 200", r.status_code == 200)

        r = await c.post(
            f"/api/tasks/{task_id}/status",
            json={"state": "approved"},
            headers=thdr,
        )
        check("status from terminal → 409", r.status_code == 409, r.text)

        r = await c.get("/api/tasks/pending", headers=thdr)
        check("pending excludes completed", len(r.json()["tasks"]) == 0)

        r = await c.post(
            "/api/tasks/install-model",
            json={
                "service_id": "sd-forge",
                "url": "https://civitai.com/api/download/models/123",
                "display_name": "Reject test",
            },
            headers=ahdr,
        )
        reject_id = r.json()["task_id"]
        r = await c.post(
            f"/api/tasks/{reject_id}/status",
            json={"state": "rejected"},
            headers=thdr,
        )
        check("reject pending→rejected → 200", r.status_code == 200, r.text)

        r = await c.post(
            "/api/tasks/install-model",
            json={
                "service_id": "sd-forge",
                "url": "https://huggingface.co/x/y.safetensors",
                "display_name": "Revoke test",
            },
            headers=ahdr,
        )
        revoke_id = r.json()["task_id"]
        r = await c.delete(f"/api/tasks/{revoke_id}", headers=ahdr)
        check("DELETE revoke pending → 200", r.status_code == 200, r.text)
        r = await c.delete(f"/api/tasks/{revoke_id}", headers=ahdr)
        check("DELETE revoked-already → 409", r.status_code == 409)

        r = await c.get(
            "/api/tasks/00000000-0000-0000-0000-000000000000",
            headers=ahdr,
        )
        check("agent GET unknown → 404", r.status_code == 404)

        check(
            "PairResponse accepts remote_tasks_token",
            "remote_tasks_token" in relay.PairResponse.model_fields,
        )


asyncio.run(main())
shutil.rmtree(TEST_DIR, ignore_errors=True)
print()
if failures:
    print(f"FAILED: {len(failures)} checks: {failures}")
    sys.exit(1)
print(f"all checks passed ({len(failures)}=0 failures)")
