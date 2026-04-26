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

# Check root
if [ "$EUID" -ne 0 ]; then
    echo -e "${YELLOW}Please run as root: sudo bash${NC}"
    exit 1
fi

RELAY_DIR="/opt/tensorlay-relay"
SERVICE_NAME="tensorlay-relay"
RELAY_URL="https://tensorlay.com/relay.py"

echo -e "${GREEN}[1/5]${NC} Installing dependencies..."
apt-get update -qq
apt-get install -y -qq python3 python3-pip > /dev/null 2>&1
pip3 install fastapi uvicorn --quiet 2>/dev/null || pip3 install fastapi uvicorn --quiet --break-system-packages 2>/dev/null

echo -e "${GREEN}[2/5]${NC} Setting up ${RELAY_DIR}..."
mkdir -p "$RELAY_DIR"
curl -sL -H "Accept: text/plain" "$RELAY_URL" -o "$RELAY_DIR/relay.py"
# Verify it's actually Python, not HTML
if head -1 "$RELAY_DIR/relay.py" | grep -q "^<"; then
    echo -e "${YELLOW}Warning: relay.py downloaded as HTML. Trying direct copy...${NC}"
    # Fallback: if running on the same server, copy directly
    if [ -f "/var/www/html/tensorlay/relay.py" ]; then
        cp /var/www/html/tensorlay/relay.py "$RELAY_DIR/relay.py"
    fi
fi
chmod +x "$RELAY_DIR/relay.py"

echo -e "${GREEN}[3/5]${NC} Generating pairing code..."
PAIRING_CODE=$(python3 -c "import secrets; print(secrets.token_hex(4).upper())")
echo "$PAIRING_CODE" > "$RELAY_DIR/pairing_code"

echo -e "${GREEN}[4/5]${NC} Creating systemd service..."
cat > /etc/systemd/system/${SERVICE_NAME}.service << EOF
[Unit]
Description=TensorLay Relay
After=network.target

[Service]
Type=simple
ExecStart=/usr/bin/python3 ${RELAY_DIR}/relay.py
Restart=always
RestartSec=5
WorkingDirectory=${RELAY_DIR}

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "$SERVICE_NAME" > /dev/null 2>&1
systemctl restart "$SERVICE_NAME"

echo -e "${GREEN}[5/5]${NC} Configuring firewall..."
if command -v ufw > /dev/null 2>&1; then
    ufw allow 8090/tcp > /dev/null 2>&1 || true
    echo "  UFW: port 8090 opened"
fi

echo ""
echo -e "${GREEN}${BOLD}=== TensorLay Relay installed! ===${NC}"
echo ""
echo -e "  Your pairing code:  ${CYAN}${BOLD}${PAIRING_CODE}${NC}"
echo ""
echo -e "  Enter this code in TensorLay desktop app to connect."
echo -e "  The code is ${YELLOW}single-use${NC} — it will expire after pairing."
echo ""
echo -e "  To generate a new code:  ${CYAN}python3 ${RELAY_DIR}/relay.py --new-code${NC}"
echo -e "  Service status:           ${CYAN}systemctl status ${SERVICE_NAME}${NC}"
echo -e "  Logs:                     ${CYAN}journalctl -u ${SERVICE_NAME} -f${NC}"
echo ""
