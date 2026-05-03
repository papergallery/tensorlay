#!/bin/bash
# TensorLay Relay — one-command VPS installer
# Usage: curl -sL https://tensorlay.com/install.sh | sudo bash
set -e

GREEN='\033[0;32m'
CYAN='\033[0;36m'
YELLOW='\033[1;33m'
BOLD='\033[1m'
NC='\033[0m'

echo -e "${CYAN}${BOLD}"
echo "  _____ _____ _   _ ___  ___  ____  _      _ __   __"
echo " |_   _| ____| \ | / __|/ _ \|  _ \| |    / \\ \ / /"
echo "   | | |  _| |  \| \__ | | | | |_) | |   / _ \\ V / "
echo "   | | | |___| |\  |__) | |_| |  _ <| |__/ ___ \| |  "
echo "   |_| |_____|_| \_|___/ \___/|_| \_|____/_/   \_|_|  "
echo ""
echo -e "  ${NC}${BOLD}VPS Relay Installer${NC}"
echo ""

if [ "$EUID" -ne 0 ]; then
    echo -e "${YELLOW}Please run as root: sudo bash${NC}"
    exit 1
fi

RELAY_USER="tensorlay"
RELAY_HOME="/home/${RELAY_USER}"
RELAY_DIR="/opt/tensorlay-relay"
VENV_DIR="${RELAY_DIR}/venv"
SERVICE_NAME="tensorlay-relay"
RELAY_URL="https://tensorlay.com/relay.py"

# Pinned versions — bump intentionally, not by accident.
FASTAPI_VERSION="0.115.6"
UVICORN_VERSION="0.32.1"
SLOWAPI_VERSION="0.1.9"
PYDANTIC_VERSION="2.10.4"
PYYAML_VERSION="6.0.2"   # v1.3.0+ — config.yaml parsing

echo -e "${GREEN}[1/8]${NC} Installing system dependencies..."
apt-get update -qq
apt-get install -y -qq python3 python3-venv python3-pip curl > /dev/null 2>&1

echo -e "${GREEN}[2/8]${NC} Creating ${RELAY_USER} system user..."
if ! id -u "${RELAY_USER}" > /dev/null 2>&1; then
    # System user, no login shell, dedicated home for ~/.ssh/authorized_keys.
    useradd \
        --system \
        --create-home \
        --home-dir "${RELAY_HOME}" \
        --shell /usr/sbin/nologin \
        --comment "TensorLay relay" \
        "${RELAY_USER}"
    echo "  Created user ${RELAY_USER}"
else
    echo "  User ${RELAY_USER} already exists"
fi

# .ssh dir owned by relay user, mode 700.
install -d -m 0700 -o "${RELAY_USER}" -g "${RELAY_USER}" "${RELAY_HOME}/.ssh"
# Ensure authorized_keys exists with correct ownership/permissions even
# before the first /pair call, so SSH won't refuse it later.
touch "${RELAY_HOME}/.ssh/authorized_keys"
chown "${RELAY_USER}:${RELAY_USER}" "${RELAY_HOME}/.ssh/authorized_keys"
chmod 0600 "${RELAY_HOME}/.ssh/authorized_keys"

echo -e "${GREEN}[3/8]${NC} Setting up ${RELAY_DIR}..."
mkdir -p "${RELAY_DIR}"
curl -sL -H "Accept: text/plain" "${RELAY_URL}" -o "${RELAY_DIR}/relay.py"
# Verify it's actually Python, not HTML (CDN/cache misroute).
if head -1 "${RELAY_DIR}/relay.py" | grep -q "^<"; then
    echo -e "${YELLOW}Warning: relay.py downloaded as HTML. Trying direct copy...${NC}"
    if [ -f "/var/www/html/tensorlay/relay.py" ]; then
        cp /var/www/html/tensorlay/relay.py "${RELAY_DIR}/relay.py"
    else
        echo -e "${YELLOW}Cannot recover relay.py. Aborting.${NC}"
        exit 1
    fi
fi
chmod +x "${RELAY_DIR}/relay.py"

# Seed config.yaml on first install. Idempotent — never overwrites a
# config the user has already customized.
CONFIG_URL="https://tensorlay.com/config.yaml.example"
if [ ! -f "${RELAY_DIR}/config.yaml" ]; then
    if curl -fsSL -H "Accept: text/plain" "${CONFIG_URL}" -o "${RELAY_DIR}/config.yaml" 2>/dev/null \
       && ! head -1 "${RELAY_DIR}/config.yaml" | grep -q "^<"; then
        echo "  Wrote default config.yaml from template"
    else
        # Fallback: leave the file absent. relay.py uses compiled-in defaults
        # in that case, so the relay still works — just without admin
        # customization until they create the file by hand.
        rm -f "${RELAY_DIR}/config.yaml"
        echo "  config.yaml.example unavailable — relay will use built-in defaults"
    fi
fi

echo -e "${GREEN}[4/8]${NC} Installing Python dependencies (venv)..."
python3 -m venv "${VENV_DIR}"
"${VENV_DIR}/bin/pip" install --quiet --upgrade pip
"${VENV_DIR}/bin/pip" install --quiet \
    "fastapi==${FASTAPI_VERSION}" \
    "uvicorn==${UVICORN_VERSION}" \
    "slowapi==${SLOWAPI_VERSION}" \
    "pydantic==${PYDANTIC_VERSION}" \
    "pyyaml==${PYYAML_VERSION}"

# Relay user owns the entire RELAY_DIR (relay.py, venv, pairing_code, service_token,
# tasks.db, agent_token, remote_tasks_token, config.yaml).
chown -R "${RELAY_USER}:${RELAY_USER}" "${RELAY_DIR}"

echo -e "${GREEN}[5/8]${NC} Generating pairing code..."
PAIRING_CODE=$(python3 -c "import secrets; print(secrets.token_hex(4).upper())")
echo "${PAIRING_CODE}" > "${RELAY_DIR}/pairing_code"
chown "${RELAY_USER}:${RELAY_USER}" "${RELAY_DIR}/pairing_code"
chmod 0600 "${RELAY_DIR}/pairing_code"

# Issue agent token if absent. Re-running install.sh keeps the existing
# token (idempotent) — admin rotates explicitly via:
#   sudo -u tensorlay /opt/tensorlay-relay/venv/bin/python relay.py --issue-agent-token
echo -e "${GREEN}[6/8]${NC} Issuing agent token..."
AGENT_TOKEN=""
if [ ! -f "${RELAY_DIR}/agent_token" ]; then
    AGENT_TOKEN=$(sudo -u "${RELAY_USER}" "${VENV_DIR}/bin/python" \
        "${RELAY_DIR}/relay.py" --issue-agent-token 2>/dev/null \
        | grep -oP '(?<=Agent token: )\S+' || true)
    if [ -z "${AGENT_TOKEN}" ]; then
        echo -e "  ${YELLOW}warning: --issue-agent-token did not return a value${NC}"
    else
        echo "  Generated new agent token (printed at end of install)"
    fi
else
    echo "  Agent token already exists — keeping as-is"
fi

echo -e "${GREEN}[7/8]${NC} Creating systemd service..."
cat > /etc/systemd/system/${SERVICE_NAME}.service <<EOF
[Unit]
Description=TensorLay Relay
After=network.target

[Service]
Type=simple
User=${RELAY_USER}
Group=${RELAY_USER}
WorkingDirectory=${RELAY_DIR}
ExecStart=${VENV_DIR}/bin/python ${RELAY_DIR}/relay.py
Restart=always
RestartSec=5

# Hardening
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=read-only
ReadWritePaths=${RELAY_DIR} ${RELAY_HOME}/.ssh
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictSUIDSGID=true
LockPersonality=true
# Defense-in-depth: relay only needs TCP sockets (HTTP server + localhost
# health probes), so block exotic socket families and syscalls. Without
# these, a hypothetical RCE in FastAPI/uvicorn could open raw/netlink
# sockets or shell out to weird syscalls.
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX
SystemCallFilter=@system-service
SystemCallErrorNumber=EPERM
MemoryDenyWriteExecute=true

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "${SERVICE_NAME}" > /dev/null 2>&1
systemctl restart "${SERVICE_NAME}"

echo -e "${GREEN}[8/8]${NC} Configuring firewall..."
if command -v ufw > /dev/null 2>&1; then
    ufw allow 8090/tcp > /dev/null 2>&1 || true
    echo "  UFW: port 8090 opened (rate-limited by relay)"
fi

echo ""
echo -e "${GREEN}${BOLD}=== TensorLay Relay installed! ===${NC}"
echo ""
echo -e "  Your pairing code:  ${CYAN}${BOLD}${PAIRING_CODE}${NC}"
echo ""
echo -e "  Enter this code in TensorLay desktop app to connect."
echo -e "  The code is ${YELLOW}single-use${NC} — it will expire after pairing."
echo ""
if [ -n "${AGENT_TOKEN}" ]; then
    echo -e "  ${BOLD}Agent token (for remote install requests, v0.9.0+):${NC}"
    echo -e "  ${CYAN}${BOLD}${AGENT_TOKEN}${NC}"
    echo ""
    echo -e "  Pass this to the agent (e.g. Claude Code on the VPS) via:"
    echo -e "    ${CYAN}export TENSORLAY_AGENT_TOKEN='${AGENT_TOKEN}'${NC}"
    echo -e "  Rotate later with: ${CYAN}sudo -u ${RELAY_USER} ${VENV_DIR}/bin/python ${RELAY_DIR}/relay.py --issue-agent-token${NC}"
    echo ""
fi
echo -e "  Pairing keys are installed under user ${CYAN}${RELAY_USER}${NC} with"
echo -e "  ${CYAN}restrict,permitlisten=...${NC} options — no shell access."
echo ""
echo -e "  To generate a new code:  ${CYAN}sudo -u ${RELAY_USER} ${VENV_DIR}/bin/python ${RELAY_DIR}/relay.py --new-code${NC}"
echo -e "  Service status:           ${CYAN}systemctl status ${SERVICE_NAME}${NC}"
echo -e "  Logs:                     ${CYAN}journalctl -u ${SERVICE_NAME} -f${NC}"
echo ""
