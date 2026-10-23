# TensorLay command bridge ("super-bridge")

Lets an AI agent on the VPS run commands, move files, and start/stop services
on the paired PC — the same capability as the thechest Unity MCP bridge
(`tools/umcp.py` → `127.0.0.1:6510`), but built into TensorLay so it rides the
SSH tunnel the app already maintains instead of a hand-run `ssh -R`.

## Trust model

Remote code execution on the user's PC, by design. Every layer is opt-in:

- **Off by default.** Enable in the desktop app: Settings → Command bridge →
  "Let the VPS agent run commands on this PC". The HTTP listener only runs
  while the app is open *and* the box is checked.
- **Loopback only.** Binds `127.0.0.1:6520`. The only other way in is the SSH
  tunnel, whose relay `permitlisten` pins `127.0.0.1:6520`.
- **Bearer token** on every request. Generated on first enable, shown in
  Settings, DPAPI-encrypted at rest. Copy it to the VPS as
  `~/.tensorlay_bridge_token` (`chmod 600`). Regenerate rotates it.
- **Visible.** A red banner in the app's title bar shows while the bridge is
  live, with a one-click **Stop**. Every request is written to the activity log.

## Server (desktop)

`app/TensorLay/Services/RemoteBridgeService.cs`. Endpoints (all JSON, all need
`Authorization: Bearer <token>` except `/bridge/ping`):

| Endpoint | Body | Returns |
|---|---|---|
| `GET /bridge/ping` | — | `{ok, service, version}` (no auth) |
| `GET /bridge/health` | — | hostname, user, cwd |
| `POST /bridge/exec` | `{command, shell, cwd, timeoutSec}` | `{exitCode, stdout, stderr, timedOut, truncated, durationMs}` |
| `POST /bridge/spawn` | `{command, shell, cwd}` | `{pid}` (detached) |
| `GET /bridge/processes` | — | spawned processes + alive/exit |
| `POST /bridge/kill` | `{pid, tree}` | `{ok}` |
| `POST /bridge/put?path=` | raw bytes | `{path, bytes}` |
| `GET /bridge/get?path=` | — | raw bytes |
| `GET /bridge/services` | — | AI services + state |
| `POST /bridge/service/start` \| `/stop` | `{id}` | `{ok, id}` |
| `GET /bridge/gpu` | — | GPU snapshot |

`exec` runs synchronously and **kills the whole process tree on timeout** — it
never hangs the way thechest's `execute_code` does past ~30s. Output is drained
asynchronously, so a chatty command can't deadlock on a full pipe. Long-lived
processes (servers) go through `spawn`, which is fully detached.

`shell` depends on the machine — `GET /bridge/health` reports `os` and `shells`
(bridge 1.1.0+):

- **Windows:** `powershell` (default) or `cmd`. PowerShell commands are sent via
  `-EncodedCommand`, so arbitrary quotes, newlines and pipes survive and UTF-8
  (Cyrillic paths) is captured intact.
- **macOS:** `zsh` (default), `bash` or `sh`. zsh/bash run as login shells
  (`-l -c`), so Homebrew and the user's PATH are there; the command is passed as
  one argv entry — no quoting layer. A request for `powershell`/`cmd` from an
  older client falls back to zsh.

Omit `shell` to get the machine's default on any version.

## Client (VPS)

`tlbridge.py` — stdlib only. Examples:

```bash
python3 tlbridge.py ping
python3 tlbridge.py exec "nvidia-smi --query-gpu=name,memory.used --format=csv"
python3 tlbridge.py exec "Get-Process | Sort-Object CPU -desc | Select -First 5"
python3 tlbridge.py spawn "C:\dev\run-server.ps1"      # detached, returns pid
python3 tlbridge.py put ./model.safetensors "C:/ai-hub/comfyui/models/checkpoints/model.safetensors"
python3 tlbridge.py get "C:/Users/me/AppData/Roaming/TensorLay/crash.log" ./crash.log
python3 tlbridge.py services
python3 tlbridge.py svc start comfyui
python3 tlbridge.py gpu
```

Token from `$TL_BRIDGE_TOKEN` or `~/.tensorlay_bridge_token`. Base URL from
`$TL_BRIDGE_URL` (default `http://127.0.0.1:6520`).

## Relay

`relay.py` adds `127.0.0.1:6520` (`BRIDGE_PORT`) to the paired client's
`permitlisten`. **Existing pairings don't pick this up automatically** — the
port list is written to `authorized_keys` at `/pair` time. After deploying
relay ≥ 1.7.0, either re-pair (Settings → Re-pair…) or append
`,permitlisten="127.0.0.1:6520"` to the client's line in
`/home/tensorlay/.ssh/authorized_keys` (no sshd restart needed).
