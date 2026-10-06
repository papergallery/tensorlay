#!/usr/bin/env python3
"""Drive the TensorLay command bridge on the paired PC from the VPS shell.

The desktop app (Settings → Command bridge ON) binds an HTTP server on the
PC's 127.0.0.1:6520 and forwards it over the SSH tunnel, so on the VPS the
same address reaches the PC. This is the TensorLay counterpart of thechest's
tools/umcp.py — a thin client so an agent can use the bridge without an MCP
client.

  tlbridge.py ping                         — liveness (no token needed)
  tlbridge.py health                        — hostname, user, cwd on the PC
  tlbridge.py exec "<command>" [--shell powershell|cmd] [--timeout 60] [--cwd DIR]
  tlbridge.py spawn "<command>" [--shell ...] [--cwd DIR]   — detached, returns pid
  tlbridge.py ps                            — processes started via spawn
  tlbridge.py kill <pid> [--no-tree]
  tlbridge.py put <local> <remote-on-pc>    — upload a file to the PC
  tlbridge.py get <remote-on-pc> <local>    — download a file from the PC
  tlbridge.py services                      — list AI services + state
  tlbridge.py svc start|stop <id>
  tlbridge.py gpu                           — GPU snapshot

Token: read from $TL_BRIDGE_TOKEN or ~/.tensorlay_bridge_token (chmod 600).
Base URL: $TL_BRIDGE_URL or http://127.0.0.1:6520
"""
import argparse
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request

BASE = os.environ.get("TL_BRIDGE_URL", "http://127.0.0.1:6520").rstrip("/")
TIMEOUT = float(os.environ.get("TL_BRIDGE_HTTP_TIMEOUT", "3600"))


def token():
    t = os.environ.get("TL_BRIDGE_TOKEN")
    if t:
        return t.strip()
    path = os.path.expanduser("~/.tensorlay_bridge_token")
    try:
        with open(path) as f:
            return f.read().strip()
    except OSError:
        sys.exit(f"No bridge token: set $TL_BRIDGE_TOKEN or write {path}")


def call(method, path, body=None, auth=True, raw_body=None, raw_out=False):
    url = BASE + path
    headers = {}
    if auth:
        headers["Authorization"] = "Bearer " + token()
    data = None
    if raw_body is not None:
        data = raw_body
        headers["Content-Type"] = "application/octet-stream"
    elif body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=TIMEOUT) as r:
            payload = r.read()
    except urllib.error.HTTPError as e:
        payload = e.read()
        if raw_out:
            sys.stderr.write(payload.decode("utf-8", "replace") + "\n")
            sys.exit(1)
        try:
            msg = json.loads(payload)
        except Exception:
            msg = {"error": payload.decode("utf-8", "replace"), "status": e.code}
        print(json.dumps(msg, ensure_ascii=False, indent=2))
        sys.exit(1)
    except urllib.error.URLError as e:
        sys.exit(f"Cannot reach bridge at {BASE}: {e.reason}. "
                 "Is TensorLay open, the bridge toggle on, and the tunnel up?")
    if raw_out:
        return payload
    return json.loads(payload) if payload else {}


def show(obj):
    print(json.dumps(obj, ensure_ascii=False, indent=2))


def main():
    p = argparse.ArgumentParser(description="TensorLay command bridge client")
    sub = p.add_subparsers(dest="cmd", required=True)

    sub.add_parser("ping")
    sub.add_parser("health")
    sub.add_parser("ps")
    sub.add_parser("services")
    sub.add_parser("gpu")

    pe = sub.add_parser("exec")
    pe.add_argument("command")
    pe.add_argument("--shell", default="powershell", choices=["powershell", "cmd"])
    pe.add_argument("--timeout", type=int, default=60)
    pe.add_argument("--cwd", default="")

    psp = sub.add_parser("spawn")
    psp.add_argument("command")
    psp.add_argument("--shell", default="powershell", choices=["powershell", "cmd"])
    psp.add_argument("--cwd", default="")

    pk = sub.add_parser("kill")
    pk.add_argument("pid", type=int)
    pk.add_argument("--no-tree", action="store_true")

    pp = sub.add_parser("put")
    pp.add_argument("local")
    pp.add_argument("remote")

    pg = sub.add_parser("get")
    pg.add_argument("remote")
    pg.add_argument("local")

    psv = sub.add_parser("svc")
    psv.add_argument("action", choices=["start", "stop"])
    psv.add_argument("id")

    a = p.parse_args()

    if a.cmd == "ping":
        show(call("GET", "/bridge/ping", auth=False))
    elif a.cmd == "health":
        show(call("GET", "/bridge/health"))
    elif a.cmd == "ps":
        show(call("GET", "/bridge/processes"))
    elif a.cmd == "services":
        show(call("GET", "/bridge/services"))
    elif a.cmd == "gpu":
        show(call("GET", "/bridge/gpu"))
    elif a.cmd == "exec":
        res = call("POST", "/bridge/exec", {
            "command": a.command, "shell": a.shell,
            "timeoutSec": a.timeout, "cwd": a.cwd})
        # Print streams plainly (that's what a caller wants), then a summary.
        if res.get("stdout"):
            sys.stdout.write(res["stdout"])
            if not res["stdout"].endswith("\n"):
                sys.stdout.write("\n")
        if res.get("stderr"):
            sys.stderr.write(res["stderr"])
        if res.get("timedOut"):
            sys.stderr.write(f"[timed out after {a.timeout}s, process killed]\n")
            sys.exit(124)
        code = res.get("exitCode")
        if res.get("truncated"):
            sys.stderr.write("[output truncated]\n")
        sys.exit(code if isinstance(code, int) else 0)
    elif a.cmd == "spawn":
        show(call("POST", "/bridge/spawn", {
            "command": a.command, "shell": a.shell, "cwd": a.cwd}))
    elif a.cmd == "kill":
        show(call("POST", "/bridge/kill", {"pid": a.pid, "tree": not a.no_tree}))
    elif a.cmd == "put":
        with open(a.local, "rb") as f:
            data = f.read()
        q = urllib.parse.urlencode({"path": a.remote})
        show(call("POST", "/bridge/put?" + q, raw_body=data))
    elif a.cmd == "get":
        q = urllib.parse.urlencode({"path": a.remote})
        data = call("GET", "/bridge/get?" + q, raw_out=True)
        with open(a.local, "wb") as f:
            f.write(data)
        print(f"wrote {len(data)} bytes to {a.local}")
    elif a.cmd == "svc":
        show(call("POST", f"/bridge/service/{a.action}", {"id": a.id}))


if __name__ == "__main__":
    main()
