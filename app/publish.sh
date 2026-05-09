#!/bin/bash
# Build, sign, publish TensorLay
# Usage: ./publish.sh [version]

set -e

VERSION="${1:-$(grep '<Version>' TensorLay/TensorLay.csproj | sed 's/.*<Version>\(.*\)<\/Version>.*/\1/')}"
PROJECT_DIR="$(cd "$(dirname "$0")" && pwd)"
PUBLISH_DIR="$PROJECT_DIR/publish"
UPDATE_DIR="/var/www/html/gpuhub/updates"
CERT_DIR="$PROJECT_DIR/../certs"
DOTNET="$HOME/.dotnet/dotnet"

echo "[+] Building TensorLay v${VERSION}..."

# 1. Update version everywhere
sed -i "s|<Version>.*</Version>|<Version>${VERSION}</Version>|" "$PROJECT_DIR/TensorLay/TensorLay.csproj"
sed -i "s|!define VERSION \".*\"|!define VERSION \"${VERSION}\"|" "$PROJECT_DIR/installer.nsi"

# 2. Build
cd "$PROJECT_DIR"
$DOTNET publish TensorLay/TensorLay.csproj -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableWindowsTargeting=true \
    -o "$PUBLISH_DIR" 2>&1 | tail -3

# 3. Sign exe
echo "[+] Signing exe..."
osslsigncode sign \
    -certs "$CERT_DIR/gpuhub_cert.pem" \
    -key "$CERT_DIR/gpuhub_key.pem" \
    -n "TensorLay" \
    -i "https://tensorlay.com" \
    -t http://timestamp.digicert.com \
    -in "$PUBLISH_DIR/GpuHub.exe" \
    -out "$PUBLISH_DIR/GpuHub_signed.exe" 2>&1 | tail -3
mv "$PUBLISH_DIR/GpuHub_signed.exe" "$PUBLISH_DIR/GpuHub.exe"

# 4. Build NSIS installer
echo "[+] Building installer..."
makensis -V2 installer.nsi 2>&1 | tail -3

# 5. Sign installer
osslsigncode sign \
    -certs "$CERT_DIR/gpuhub_cert.pem" \
    -key "$CERT_DIR/gpuhub_key.pem" \
    -n "TensorLay Setup" \
    -i "https://tensorlay.com" \
    -t http://timestamp.digicert.com \
    -in "$PROJECT_DIR/TensorLay-Setup.exe" \
    -out "$PROJECT_DIR/TensorLay-Setup_signed.exe" 2>&1 | tail -3
mv "$PROJECT_DIR/TensorLay-Setup_signed.exe" "$PROJECT_DIR/TensorLay-Setup.exe"

# 6. Deploy
# $UPDATE_DIR is root-owned (security fix for #C3 — any process running as the
# web/dev user could otherwise overwrite update artifacts and ship arbitrary
# code to every installed client). Use sudo install to write atomically with
# explicit mode + ownership; the deploy step is the only thing in publish.sh
# that needs elevation. Falls back to interactive password prompt if the user
# doesn't have passwordless sudo configured.
sudo install -o root -g root -m 0644 "$PUBLISH_DIR/GpuHub.exe" "$UPDATE_DIR/GpuHub.exe"
sudo install -o root -g root -m 0644 "$PROJECT_DIR/TensorLay-Setup.exe" "$UPDATE_DIR/TensorLay-Setup.exe"
sudo install -o root -g root -m 0644 "$CERT_DIR/gpuhub_cert.pem" "$UPDATE_DIR/gpuhub_cert.cer"

# Compute SHA256 over the SIGNED exe so the client can verify integrity.
EXE_SHA256=$(sha256sum "$UPDATE_DIR/GpuHub.exe" | awk '{print $1}')

# Write version.json via sudo tee — same root-ownership constraint as the exes.
# Trailing chmod is belt-and-braces; tee preserves the existing 0644 anyway.
sudo tee "$UPDATE_DIR/version.json" > /dev/null << EOF
{
    "version": "${VERSION}",
    "changelog": "Update to v${VERSION}",
    "url": "https://tensorlay.com/updates/GpuHub.exe",
    "sha256": "${EXE_SHA256}"
}
EOF
sudo chmod 0644 "$UPDATE_DIR/version.json"
sudo chown root:root "$UPDATE_DIR/version.json"

echo ""
echo "[+] Published v${VERSION}"
echo "    Exe:       $(du -h "$UPDATE_DIR/GpuHub.exe" | cut -f1)"
echo "    Installer: $(du -h "$UPDATE_DIR/TensorLay-Setup.exe" | cut -f1)"
echo "    SHA256:    ${EXE_SHA256}"
echo "    Download:  https://tensorlay.com/download"
