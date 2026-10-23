#!/bin/bash
# Build, sign and publish TensorLay for macOS (Apple Silicon).
# Usage: ./publish-mac.sh [version] [--channel stable|test] [--no-deploy]
#
#   stable (default) -> https://tensorlay.com/updates/mac/       (auto-update feed)
#   test             -> https://tensorlay.com/updates/mac-test/  (CI smoke test / manual
#                       testing; the app never polls it)
#
# Output: publish-mac/TensorLay.app (ad-hoc signed) and publish-mac/TensorLay-mac.zip.
# The bundle is signed with rcodesign (apple-codesign, runs on Linux): an ad-hoc
# signature is the minimum Apple Silicon needs to execute the code at all. It is
# not a Developer ID signature, so the first launch of a browser-downloaded copy
# goes through System Settings -> Privacy & Security -> "Open Anyway". Updates
# fetched by the app itself carry no quarantine flag and open directly.

set -e
set -o pipefail

PROJECT_DIR="$(cd "$(dirname "$0")" && pwd)"
CSPROJ="$PROJECT_DIR/TensorLay.Mac/TensorLay.Mac.csproj"
OUT="$PROJECT_DIR/publish-mac"
DOTNET="$HOME/.dotnet/dotnet"
RCODESIGN="${RCODESIGN:-$(ls "$HOME"/opt/rcodesign/*/rcodesign 2>/dev/null | head -1)}"
RID="osx-arm64"

VERSION=""
CHANNEL="stable"
DEPLOY=1
while [ $# -gt 0 ]; do
    case "$1" in
        --channel) CHANNEL="$2"; shift 2 ;;
        --no-deploy) DEPLOY=0; shift ;;
        *) VERSION="$1"; shift ;;
    esac
done
VERSION="${VERSION:-$(grep '<Version>' "$CSPROJ" | sed 's/.*<Version>\(.*\)<\/Version>.*/\1/')}"
case "$CHANNEL" in
    stable) UPDATE_DIR="/var/www/html/gpuhub/updates/mac"; URL_DIR="mac" ;;
    test)   UPDATE_DIR="/var/www/html/gpuhub/updates/mac-test"; URL_DIR="mac-test" ;;
    *) echo "unknown channel: $CHANNEL" >&2; exit 2 ;;
esac

[ -x "$RCODESIGN" ] || { echo "rcodesign not found (set RCODESIGN=...)" >&2; exit 1; }

echo "[+] Building TensorLay for macOS v${VERSION} (${RID}, channel ${CHANNEL})..."
sed -i "s|<Version>.*</Version>|<Version>${VERSION}</Version>|" "$CSPROJ"

rm -rf "$OUT"
mkdir -p "$OUT/bin"
$DOTNET publish "$CSPROJ" -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true \
    -p:DebugType=None \
    -o "$OUT/bin" 2>&1 | tail -3

# ── Bundle ────────────────────────────────────────────────────────────────
APP="$OUT/TensorLay.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$OUT/bin/TensorLay" "$APP/Contents/MacOS/"
cp "$OUT"/bin/*.dylib "$APP/Contents/MacOS/"
chmod 755 "$APP/Contents/MacOS/TensorLay"

python3 "$PROJECT_DIR/TensorLay.Mac/Packaging/make_icon.py" "$OUT/icon" >/dev/null
cp "$OUT/icon/TensorLay.icns" "$APP/Contents/Resources/TensorLay.icns"

cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key><string>com.tensorlay.app</string>
    <key>CFBundleName</key><string>TensorLay</string>
    <key>CFBundleDisplayName</key><string>TensorLay</string>
    <key>CFBundleExecutable</key><string>TensorLay</string>
    <key>CFBundleIconFile</key><string>TensorLay</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>${VERSION}</string>
    <key>CFBundleVersion</key><string>${VERSION}</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
    <key>NSHighResolutionCapable</key><true/>
    <!-- Background tunnel/bridge app: no App Nap (see AppNap.cs). -->
    <key>NSAppSleepDisabled</key><true/>
    <!-- The UI is light-only (Wispr palette); keep the window chrome light too. -->
    <key>NSRequiresAquaSystemAppearance</key><true/>
    <key>CFBundleURLTypes</key>
    <array>
        <dict>
            <key>CFBundleURLName</key><string>com.tensorlay.app</string>
            <key>CFBundleURLSchemes</key><array><string>tensorlay</string></array>
        </dict>
    </array>
</dict>
</plist>
EOF

# ── Sign (ad-hoc) ─────────────────────────────────────────────────────────
echo "[+] Signing bundle (ad-hoc)..."
"$RCODESIGN" sign "$APP" 2>&1 | tail -2

# ── Zip (keeps exec bits; ditto -x -k on the Mac restores them) ───────────
echo "[+] Zipping..."
python3 - "$OUT" <<'PY'
import os, sys, zipfile
out = sys.argv[1]
zpath = os.path.join(out, "TensorLay-mac.zip")
with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as z:
    for root, dirs, files in os.walk(os.path.join(out, "TensorLay.app")):
        dirs.sort()
        for name in sorted(dirs + files):
            full = os.path.join(root, name)
            arc = os.path.relpath(full, out) + ("/" if os.path.isdir(full) else "")
            info = zipfile.ZipInfo(arc)
            st = os.lstat(full)
            info.external_attr = (st.st_mode & 0xFFFF) << 16
            info.create_system = 3  # Unix, so the mode bits are honoured
            if os.path.isdir(full):
                z.writestr(info, b"")
            elif name in files:
                info.compress_type = zipfile.ZIP_DEFLATED
                with open(full, "rb") as f:
                    z.writestr(info, f.read())
PY

ZIP_SHA256=$(sha256sum "$OUT/TensorLay-mac.zip" | awk '{print $1}')
echo "    Zip:    $(du -h "$OUT/TensorLay-mac.zip" | cut -f1)  sha256 ${ZIP_SHA256}"

if [ "$DEPLOY" = "0" ]; then
    echo "[+] Built v${VERSION} (not deployed)."
    exit 0
fi

# ── Deploy (root-owned update dir, same model as publish.sh) ──────────────
sudo install -d -o root -g root -m 0755 "$UPDATE_DIR"
sudo install -o root -g root -m 0644 "$OUT/TensorLay-mac.zip" "$UPDATE_DIR/TensorLay-mac.zip"
sudo tee "$UPDATE_DIR/version.json" > /dev/null << EOF
{
    "version": "${VERSION}",
    "changelog": "Update to v${VERSION}",
    "url": "https://tensorlay.com/updates/${URL_DIR}/TensorLay-mac.zip",
    "sha256": "${ZIP_SHA256}"
}
EOF
sudo chmod 0644 "$UPDATE_DIR/version.json"
sudo chown root:root "$UPDATE_DIR/version.json"

echo ""
echo "[+] Published macOS v${VERSION} (${CHANNEL})"
echo "    Download: https://tensorlay.com/updates/${URL_DIR}/TensorLay-mac.zip"
