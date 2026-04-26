#!/bin/bash
# Build, sign, publish TensorLay
# Usage: ./publish.sh [version]

set -e

VERSION="${1:-$(grep '<Version>' TensorLay/TensorLay.csproj | sed 's/.*<Version>\(.*\)<\/Version>.*/\1/')}"
PROJECT_DIR="$(cd "$(dirname "$0")" && pwd)"
PUBLISH_DIR="$PROJECT_DIR/publish"
UPDATE_DIR="/var/www/html/gpuhub/updates"
CERT_DIR="$PROJECT_DIR/certs"
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
cp "$PUBLISH_DIR/GpuHub.exe" "$UPDATE_DIR/GpuHub.exe"
cp "$PROJECT_DIR/TensorLay-Setup.exe" "$UPDATE_DIR/TensorLay-Setup.exe"
cp "$CERT_DIR/gpuhub_cert.pem" "$UPDATE_DIR/gpuhub_cert.cer"

cat > "$UPDATE_DIR/version.json" << EOF
{
    "version": "${VERSION}",
    "changelog": "Update to v${VERSION}",
    "url": "https://tensorlay.com/updates/GpuHub.exe"
}
EOF

echo ""
echo "[+] Published v${VERSION}"
echo "    Exe:       $(du -h "$UPDATE_DIR/GpuHub.exe" | cut -f1)"
echo "    Installer: $(du -h "$UPDATE_DIR/TensorLay-Setup.exe" | cut -f1)"
echo "    Download:  https://tensorlay.com/download"
