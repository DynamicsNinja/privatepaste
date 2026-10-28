#!/usr/bin/env bash
# Builds unsigned PrivatePaste.app bundles for macOS (osx-arm64 and osx-x64) and zips them.
#
# Run on macOS (needs: dotnet SDK 10, sips, iconutil, ditto; all ship with macOS / Xcode CLT).
#
# Inputs (environment variables, all optional):
#   VERSION   bundle version, default 0.0.0 (CI passes the tag without the leading "v")
#   RIDS      space-separated runtime ids, default "osx-arm64 osx-x64"
#   OUT_DIR   output directory, default <repo>/artifacts/macos
#
# Outputs, per RID, in OUT_DIR:
#   PrivatePaste-<VERSION>-<rid>.zip   containing PrivatePaste.app (bundle id xyz.ficdev.privatepaste)
#
# The app is not signed or notarized. Users open it with right-click > Open, or run
#   xattr -d com.apple.quarantine PrivatePaste.app
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${VERSION:-0.0.0}"
RIDS="${RIDS:-osx-arm64 osx-x64}"
OUT_DIR="${OUT_DIR:-$REPO/artifacts/macos}"
BUNDLE_ID="xyz.ficdev.privatepaste"
ICON_SRC="$REPO/branding/app-icon-512.png"

mkdir -p "$OUT_DIR"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# ---- .icns from the 512 px PNG (sips to resize, iconutil to pack) ----
ICNS="$WORK/PrivatePaste.icns"
if command -v iconutil >/dev/null 2>&1 && command -v sips >/dev/null 2>&1; then
  ICONSET="$WORK/PrivatePaste.iconset"
  mkdir -p "$ICONSET"
  for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$ICON_SRC" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    if [ "$double" -le 512 ]; then
      sips -z "$double" "$double" "$ICON_SRC" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
    else
      cp "$ICON_SRC" "$ICONSET/icon_${size}x${size}@2x.png"   # 1024 px slot: reuse the 512 px source
    fi
  done
  iconutil -c icns "$ICONSET" -o "$ICNS"
else
  echo "warning: iconutil/sips not found; the app bundle will have no icon" >&2
fi

for rid in $RIDS; do
  echo "== $rid =="
  PUB="$WORK/publish-$rid"
  dotnet publish "$REPO/src/PrivatePaste.Desktop" -c Release -r "$rid" --self-contained true \
    -p:Version="$VERSION" -o "$PUB"

  APP="$WORK/$rid/PrivatePaste.app"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp -R "$PUB/." "$APP/Contents/MacOS/"
  chmod +x "$APP/Contents/MacOS/PrivatePaste"
  [ -f "$ICNS" ] && cp "$ICNS" "$APP/Contents/Resources/PrivatePaste.icns"

  # LSUIElement: a tray-only app, no Dock icon or app menu.
  cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>PrivatePaste</string>
    <key>CFBundleDisplayName</key><string>PrivatePaste</string>
    <key>CFBundleIdentifier</key><string>${BUNDLE_ID}</string>
    <key>CFBundleExecutable</key><string>PrivatePaste</string>
    <key>CFBundleIconFile</key><string>PrivatePaste</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleVersion</key><string>${VERSION}</string>
    <key>CFBundleShortVersionString</key><string>${VERSION}</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>LSUIElement</key><true/>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

  ZIP="$OUT_DIR/PrivatePaste-${VERSION}-${rid}.zip"
  rm -f "$ZIP"
  ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP"
  echo "wrote $ZIP"
done
