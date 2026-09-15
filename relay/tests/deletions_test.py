"""Tests for the v1.6.0 inventory + remote model deletion endpoints.

Runs the relay app in-process against an isolated temp DATA_DIR (same
approach as smoke_test.py) and drives the agent and desktop sides of the
flow over HTTP. Exits non-zero on any unexpected response.

Run: <venv with relay deps + httpx>/bin/python relay/tests/deletions_test.py
"""
import asyncio
import json
import shutil
import sys
import tempfile
from pathlib import Path

import httpx

TEST_DIR = Path(tempfile.mkdtemp(prefix="relay-deletions-"))
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import relay  # noqa: E402

relay.DATA_DIR = TEST_DIR
relay.PAIRING_CODE_FILE = TEST_DIR / "pairing_code"
relay.SERVICE_TOKEN_FILE = TEST_DIR / "service_token"
relay.TASKS_DB_FILE = TEST_DIR / "tasks.db"
relay.AGENT_TOKEN_FILE = TEST_DIR / "agent_token"
relay.REMOTE_TASKS_TOKEN_FILE = TEST_DIR / "remote_tasks_token"
relay.CONFIG_FILE = TEST_DIR / "config.yaml"
relay.LOGS_DIR = TEST_DIR / "logs"
relay.INVENTORY_FILE = TEST_DIR / "inventory.json"
relay._init_db()
# Validation cases alone exceed the 20/hour create limit; rate limiting has
# its own coverage in smoke_test.py.
relay.limiter.enabled = False

failures: list[str] = []


def check(label: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {label}")
    else:
        print(f"  FAIL {label}: {detail}")
        failures.append(label)


async def main() -> None:
    relay._cli_issue_agent_token()
    relay._issue_remote_tasks_token()
    agent = {"Authorization": f"Bearer {relay.AGENT_TOKEN_FILE.read_text().strip()}"}
    desk = {"Authorization": f"Bearer {relay.REMOTE_TASKS_TOKEN_FILE.read_text().strip()}"}

    transport = httpx.ASGITransport(app=relay.app)
    async with httpx.AsyncClient(transport=transport, base_url="http://test") as c:
        r = await c.get("/health")
        check("health reports 1.6.0", r.json().get("version") == "1.6.0", r.text)

        # ── inventory ────────────────────────────────────────────────
        r = await c.get("/api/inventory", headers=agent)
        check("inventory before push → 404", r.status_code == 404, r.text)

        inv = {"generated_at": "2026-09-15T10:00:00Z", "services": [
            {"service_id": "comfyui", "files": [
                {"path": "checkpoints/flux1-dev-fp8.safetensors", "size_bytes": 17_000_000_000},
            ]},
        ]}
        r = await c.put("/api/inventory", content=json.dumps(inv), headers=agent)
        check("inventory push with agent token → 403", r.status_code == 403, r.text)
        r = await c.put("/api/inventory", content=b"[1,2]", headers=desk)
        check("inventory push non-object → 400", r.status_code == 400, r.text)
        r = await c.put("/api/inventory", content=b"{not json", headers=desk)
        check("inventory push bad JSON → 400", r.status_code == 400, r.text)
        r = await c.put("/api/inventory", content=b"x" * (relay.MAX_INVENTORY_BYTES + 1), headers=desk)
        check("inventory push oversize → 413", r.status_code == 413, r.text)
        r = await c.put("/api/inventory", content=json.dumps(inv), headers=desk)
        check("inventory push → 200", r.status_code == 200, r.text)
        check("inventory file is 0600", (relay.INVENTORY_FILE.stat().st_mode & 0o777) == 0o600)
        r = await c.get("/api/inventory", headers=desk)
        check("inventory read with desktop token → 403", r.status_code == 403, r.text)
        r = await c.get("/api/inventory", headers=agent)
        body = r.json()
        check("inventory read → 200 + same content",
              r.status_code == 200 and body["inventory"] == inv and body["received_at"].endswith("Z"), r.text)

        # ── validation ───────────────────────────────────────────────
        bad_items = [
            ({"service_id": "whisper", "path": "x.safetensors"}, "non-deletable service"),
            ({"service_id": "nope", "path": "x.safetensors"}, "unknown service"),
            ({"service_id": "comfyui", "path": "../x.safetensors"}, "dotdot segment"),
            ({"service_id": "comfyui", "path": "loras/../../x.safetensors"}, "nested dotdot"),
            ({"service_id": "comfyui", "path": "/checkpoints/x.safetensors"}, "leading slash"),
            ({"service_id": "comfyui", "path": "C:/Windows/x.safetensors"}, "drive letter"),
            ({"service_id": "comfyui", "path": "checkpoints\\x.safetensors"}, "backslash"),
            ({"service_id": "comfyui", "path": "checkpoints//x.safetensors"}, "empty segment"),
            ({"service_id": "comfyui", "path": "checkpoints/x.safetensors:ads"}, "alternate data stream"),
            ({"service_id": "comfyui", "path": "checkpoints/*.safetensors"}, "wildcard"),
            ({"service_id": "comfyui", "path": "extra_model_paths.yaml"}, "non-model extension"),
            ({"service_id": "comfyui", "path": "checkpoints/noext"}, "no extension"),
            ({"service_id": "comfyui", "path": "loras./x.safetensors"}, "trailing-dot segment"),
            ({"service_id": "comfyui", "path": "a/b/c/d/e/f/g/h/i.safetensors"}, "too deep"),
            ({"service_id": "ollama", "path": "qwen 3:4b"}, "ollama tag with space"),
        ]
        for item, label in bad_items:
            r = await c.post("/api/deletions", json={"items": [item]}, headers=agent)
            check(f"reject {label} → 400", r.status_code == 400, f"{r.status_code} {r.text}")

        r = await c.post("/api/deletions", json={"items": []}, headers=agent)
        check("empty item list → 422", r.status_code == 422, r.text)
        r = await c.post("/api/deletions",
                         json={"items": [{"service_id": "comfyui", "path": "checkpoints/x.safetensors"}]},
                         headers=desk)
        check("create with desktop token → 403", r.status_code == 403, r.text)

        # ── happy path ───────────────────────────────────────────────
        items = [
            {"service_id": "comfyui", "path": "checkpoints/flux1-dev-fp8.safetensors", "size_bytes": 17_000_000_000},
            {"service_id": "comfyui", "path": "insightface/models/antelopev2/glintr100.onnx"},
            {"service_id": "comfyui", "path": "loras/Retro Comic (v2).safetensors"},
            {"service_id": "comfyui", "path": "checkpoints/flux1-dev-fp8.safetensors"},  # duplicate
            {"service_id": "ollama", "path": "hf.co/unsloth/Qwen3.5-9B-GGUF:IQ4_XS"},
        ]
        r = await c.post("/api/deletions", json={"items": items, "reason": "cleanup"}, headers=agent)
        check("create → 202", r.status_code == 202, r.text)
        req_id = r.json().get("id", "")

        r = await c.get("/api/deletions/pending", headers=agent)
        check("pending with agent token → 403", r.status_code == 403, r.text)
        r = await c.get("/api/deletions/pending", headers=desk)
        reqs = r.json().get("requests", [])
        check("pending lists the request", r.status_code == 200 and [q["id"] for q in reqs] == [req_id], r.text)
        check("duplicate item collapsed", reqs and len(reqs[0]["items"]) == 4, json.dumps(reqs))
        check("pending carries reason + agent_label",
              reqs and reqs[0]["reason"] == "cleanup" and reqs[0]["agent_label"], json.dumps(reqs))

        r = await c.post(f"/api/deletions/{req_id}/result", json={"state": "approved"}, headers=desk)
        check("result with bad state → 400", r.status_code == 400, r.text)
        r = await c.post(f"/api/deletions/{req_id}/result",
                         json={"state": "completed", "results": [
                             {"service_id": "comfyui", "path": "x.safetensors", "status": "vanished"}]},
                         headers=desk)
        check("result with bad item status → 400", r.status_code == 400, r.text)

        results = [
            {"service_id": "comfyui", "path": "checkpoints/flux1-dev-fp8.safetensors", "status": "deleted", "freed_bytes": 17_000_000_000},
            {"service_id": "comfyui", "path": "insightface/models/antelopev2/glintr100.onnx", "status": "missing"},
            {"service_id": "comfyui", "path": "loras/Retro Comic (v2).safetensors", "status": "skipped"},
            {"service_id": "ollama", "path": "hf.co/unsloth/Qwen3.5-9B-GGUF:IQ4_XS", "status": "error", "error": "Ollama is not running"},
        ]
        r = await c.post(f"/api/deletions/{req_id}/result", json={"state": "completed", "results": results}, headers=desk)
        check("result → 200", r.status_code == 200, r.text)
        r = await c.post(f"/api/deletions/{req_id}/result", json={"state": "completed", "results": results}, headers=desk)
        check("second result → 409", r.status_code == 409, r.text)

        r = await c.get(f"/api/deletions/{req_id}", headers=agent)
        got = r.json()
        check("agent sees completed + results + freed_bytes",
              got.get("state") == "completed" and got.get("freed_bytes") == 17_000_000_000
              and len(got.get("results") or []) == 4, r.text)
        r = await c.get("/api/deletions/pending", headers=desk)
        check("completed request leaves pending list", r.json()["requests"] == [], r.text)

        # ── revoke / reject ──────────────────────────────────────────
        one = {"items": [{"service_id": "sd-forge", "path": "Stable-diffusion/old.ckpt"}]}
        rid = (await c.post("/api/deletions", json=one, headers=agent)).json()["id"]
        r = await c.delete(f"/api/deletions/{rid}", headers=agent)
        check("revoke pending → 200", r.status_code == 200, r.text)
        r = await c.delete(f"/api/deletions/{rid}", headers=agent)
        check("revoke again → 409", r.status_code == 409, r.text)
        r = await c.post(f"/api/deletions/{rid}/result", json={"state": "rejected"}, headers=desk)
        check("result on revoked → 409", r.status_code == 409, r.text)

        rid = (await c.post("/api/deletions", json=one, headers=agent)).json()["id"]
        r = await c.post(f"/api/deletions/{rid}/result", json={"state": "rejected", "error_msg": "toggle off"}, headers=desk)
        check("reject → 200", r.status_code == 200, r.text)
        r = await c.get(f"/api/deletions/{rid}", headers=agent)
        check("agent sees rejected + error_msg",
              r.json().get("state") == "rejected" and r.json().get("error_msg") == "toggle off", r.text)
        r = await c.get("/api/deletions/does-not-exist", headers=agent)
        check("unknown id → 404", r.status_code == 404, r.text)

    # ── expiry sweep ─────────────────────────────────────────────────
    with relay._db() as conn:
        conn.execute(
            "INSERT INTO delete_requests (id, created_at, updated_at, state, items, agent_label, expires_at) "
            "VALUES ('old-pending', 0, 0, 'pending', '[]', 'x', 1)")
        conn.execute(
            "INSERT INTO delete_requests (id, created_at, updated_at, state, items, agent_label, expires_at) "
            "VALUES ('old-done', 0, 0, 'completed', '[]', 'x', 1)")
    relay.CONFIG["tasks"]["expire_check_interval"] = 0.01
    loop_task = asyncio.create_task(relay._expire_loop())
    await asyncio.sleep(0.2)
    loop_task.cancel()
    with relay._db() as conn:
        states = {r["id"]: r["state"] for r in conn.execute("SELECT id, state FROM delete_requests")}
    check("sweep expires stale pending", states.get("old-pending") == "expired", str(states))
    check("sweep purges old terminal rows", "old-done" not in states, str(states))


try:
    asyncio.run(main())
finally:
    shutil.rmtree(TEST_DIR, ignore_errors=True)

if failures:
    print(f"\n{len(failures)} FAILED")
    sys.exit(1)
print("\nall passed")
