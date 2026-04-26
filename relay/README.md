# TensorLay Relay

Tiny FastAPI service that runs on your VPS. The TensorLay desktop app pairs with it over SSH so AI agents on the VPS can reach services running on your home GPU.

## Install on a VPS

As root on any Linux server:

```bash
curl -sL https://tensorlay.com/install.sh | sudo bash
```

The script installs `python3-pip`, drops `relay.py` into `/opt/tensorlay-relay/`, registers a `tensorlay-relay.service` systemd unit, and prints an 8-character pairing code. Open ports: `8090/tcp` (the relay) and `22/tcp` (SSH for the tunnel).

## Endpoints

| Method | Path        | Purpose |
|--------|-------------|---------|
| GET    | `/health`   | Liveness check |
| POST   | `/pair`     | Accept pairing code + SSH public key, append to `~/.ssh/authorized_keys` |
| GET    | `/services` | List AI services with their health status |
| POST   | `/new-code` | Generate a new single-use pairing code |

## Files

- `install.sh` — one-command installer (run as root)
- `relay.py` — FastAPI daemon (single file)

That's the whole relay. By design.

## Generating a new pairing code on an already-installed VPS

```bash
python3 /opt/tensorlay-relay/relay.py --new-code
```

## Service status / logs

```bash
systemctl status tensorlay-relay
journalctl -u tensorlay-relay -f
```
