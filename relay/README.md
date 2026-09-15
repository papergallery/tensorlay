# TensorLay Relay

Tiny FastAPI service that runs on your VPS. The TensorLay desktop app pairs with it over SSH so AI agents on the VPS can reach services running on your home GPU.

## Install on a VPS

As root on any Linux server:

```bash
curl -sL https://tensorlay.com/install.sh | sudo bash
```

The script installs `python3-pip`, drops `relay.py` into `/opt/tensorlay-relay/`, registers a `tensorlay-relay.service` systemd unit, and prints an 8-character pairing code. Since v1.3.0 it also issues an **agent token** (printed alongside the pair code) for remote install requests. Open ports: `8090/tcp` (the relay) and `22/tcp` (SSH for the tunnel).

## Endpoints

### Pairing & service discovery (since v1.0)

| Method | Path        | Auth          | Purpose |
|--------|-------------|---------------|---------|
| GET    | `/health`   | none          | Liveness check |
| POST   | `/pair`     | pair code     | Accept pairing code + SSH public key, append to `~/.ssh/authorized_keys` |
| GET    | `/services` | service token | List AI services with their health status |

### Remote install requests (since v1.3.0)

The agent (e.g. Claude Code on the VPS) submits download tasks; the desktop polls and shows an approval modal before any download starts. Two distinct bearer tokens — agent token (one per VPS, admin-issued via CLI) and remote-tasks token (one per paired desktop, rotated every `/pair`).

| Method | Path                              | Auth         | Purpose |
|--------|-----------------------------------|--------------|---------|
| POST   | `/api/tasks/install-model`        | agent token  | Queue a model-download task |
| GET    | `/api/tasks/{id}`                 | agent token  | Poll task state + progress |
| DELETE | `/api/tasks/{id}`                 | agent token  | Revoke a still-pending task |
| GET    | `/api/tasks/pending`              | tasks token  | Desktop polls for new tasks |
| POST   | `/api/tasks/{id}/status`          | tasks token  | Desktop reports approval / progress / outcome |

URLs in `install-model` are validated against the allowlist in `config.yaml` (defaults: HuggingFace, CivitAI, GitHub releases). Plain HTTP is rejected; only `https://` URLs are accepted.

### Remote model cleanup (since v1.6.0, desktop v0.9.27+)

With **Settings → Allow the VPS agent to see installed models and request deletions** turned on, the desktop pushes a model inventory every few minutes and polls for deletion requests. Each request opens a window listing the files with checkboxes; nothing is deleted until the user clicks, and they can untick anything. With the toggle off, no inventory is sent and requests come back `rejected`.

| Method | Path                              | Auth         | Purpose |
|--------|-----------------------------------|--------------|---------|
| GET    | `/api/inventory`                  | agent token  | Latest model inventory + `received_at` |
| POST   | `/api/deletions`                  | agent token  | Queue a deletion request (rate-limited, `20/hour` by default) |
| GET    | `/api/deletions/{id}`             | agent token  | Poll state + per-item results |
| DELETE | `/api/deletions/{id}`             | agent token  | Revoke a still-pending request |
| PUT    | `/api/inventory`                  | tasks token  | Desktop uploads its inventory |
| GET    | `/api/deletions/pending`          | tasks token  | Desktop polls for new requests |
| POST   | `/api/deletions/{id}/result`      | tasks token  | Desktop reports `completed` / `rejected` / `failed` |

Inventory shape (abridged): `{"received_at": "...Z", "inventory": {"generated_at", "app_version", "install_directory", "disk": {"root", "total_bytes", "free_bytes"}, "services": [{"service_id", "display_name", "available", "models_root", "note", "total_bytes", "files": [{"path", "size_bytes", "modified_at"}]}]}}`. The desktop re-pushes at least every 15 minutes while running, so a stale `received_at` means it is offline or the toggle is off.

Deletion request body — paths exactly as they appear in the inventory:

```json
{
  "reason": "FLUX.1 stack is unused since the switch to FLUX.2 klein",
  "items": [
    {"service_id": "comfyui", "path": "checkpoints/flux1-dev-fp8.safetensors", "size_bytes": 17000000000},
    {"service_id": "ollama",  "path": "qwen3.5:4b"}
  ]
}
```

Only `sd-forge`, `comfyui`, `alltalk` (paths relative to the service's models folder) and `ollama` (model tags, deleted through Ollama's API — it must be running) are accepted, and only model file types (`.safetensors .sft .ckpt .pt .pth .bin .gguf .onnx`). Absolute paths, `..`, backslashes, colons and wildcards are rejected here and again on the desktop, which also checks that the resolved path stays inside the models folder. Deletion is permanent (not the Recycle Bin).

Result: `state` is `pending` → `completed` (user approved; see `results[]`, each `deleted` | `missing` | `skipped` (unticked) | `error` with `error`, plus total `freed_bytes`), `rejected`, `expired` (24h) or `revoked`.

## Files

- `install.sh` — one-command installer (run as root)
- `relay.py` — FastAPI daemon (single file, ~800 LOC)
- `config.yaml.example` — allowlist + TTL config (copied to `/opt/tensorlay-relay/config.yaml` on first install)
- `tests/smoke_test.py` — end-to-end test of all endpoints against an isolated DB
- `tests/deletions_test.py` — inventory + model deletion endpoints (v1.6.0)

That's the whole relay. By design.

## CLI commands

```bash
# Generate a new single-use pairing code
sudo -u tensorlay /opt/tensorlay-relay/venv/bin/python /opt/tensorlay-relay/relay.py --new-code

# Issue (or rotate) the agent token. Prints once; not logged.
sudo -u tensorlay /opt/tensorlay-relay/venv/bin/python /opt/tensorlay-relay/relay.py --issue-agent-token
```

Rotating the agent token immediately invalidates the previous one. Pass the new value to the agent via `TENSORLAY_AGENT_TOKEN` env var.

## Service status / logs

```bash
systemctl status tensorlay-relay
journalctl -u tensorlay-relay -f
```

## Configuration

Edit `/opt/tensorlay-relay/config.yaml` (created from `config.yaml.example` on first install). Restart the service to apply.

```yaml
tasks:
  ttl_seconds: 86400              # 24h before pending tasks expire
  expire_check_interval: 300

allowlist:
  hosts:                           # hostname-suffix allowlist for install URLs
    - huggingface.co
    - civitai.com
    - github.com

agent:
  label: "VPS agent"               # shown in the desktop's approval modal
```

## Storage

- `/opt/tensorlay-relay/pairing_code` — single-use 8-char code (deleted on use)
- `/opt/tensorlay-relay/service_token` — long-lived token for `/services` (one per VPS, persists across pairings)
- `/opt/tensorlay-relay/remote_tasks_token` — desktop's bearer for `/api/tasks/*` (rotated on every `/pair`)
- `/opt/tensorlay-relay/agent_token` — agent's bearer for `/api/tasks/install-model`
- `/opt/tensorlay-relay/tasks.db` — SQLite (WAL mode) holding the task queue, deletion requests and 7-day audit history
- `/opt/tensorlay-relay/inventory.json` — latest model inventory pushed by the desktop (mode 0600)
- `/opt/tensorlay-relay/config.yaml` — admin-editable config (see above)
