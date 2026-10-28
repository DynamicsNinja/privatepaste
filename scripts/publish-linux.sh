#!/usr/bin/env bash
# Builds an unsigned linux-x64 PrivatePaste tarball with a .desktop launcher and the icon.
#
# Run on Linux (needs: dotnet SDK 10, tar).
#
# Inputs (environment variables, all optional):
#   VERSION   default 0.0.0 (CI passes the tag without the leading "v")
#   OUT_DIR   output directory, default <repo>/artifacts/linux
#
# Output in OUT_DIR:
#   PrivatePaste-<VERSION>-linux-x64.tar.gz containing PrivatePaste/
#     PrivatePaste               the self-contained executable (and its libraries)
#     privatepaste.png           icon (branding/app-icon-512.png)
#     privatepaste.desktop       launcher template: Exec/Icon use the placeholder /opt/PrivatePaste; edit them to
#                                where you unpack it, then copy to ~/.local/share/applications/
#     install.sh                 does that for you: copies the folder to ~/.local/opt/PrivatePaste and installs the launcher
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${VERSION:-0.0.0}"
OUT_DIR="${OUT_DIR:-$REPO/artifacts/linux}"
RID="linux-x64"

mkdir -p "$OUT_DIR"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

PKG="$WORK/PrivatePaste"
dotnet publish "$REPO/src/PrivatePaste.Desktop" -c Release -r "$RID" --self-contained true \
  -p:Version="$VERSION" -o "$PKG"
chmod +x "$PKG/PrivatePaste"
cp "$REPO/branding/app-icon-512.png" "$PKG/privatepaste.png"

cat > "$PKG/privatepaste.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=PrivatePaste
Comment=Private clipboard sync
Exec=/opt/PrivatePaste/PrivatePaste
Icon=/opt/PrivatePaste/privatepaste.png
Terminal=false
Categories=Utility;
DESKTOP

cat > "$PKG/install.sh" <<'INSTALL'
#!/usr/bin/env bash
# Copies this folder to ~/.local/opt/PrivatePaste and installs the application launcher.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dest="$HOME/.local/opt/PrivatePaste"
mkdir -p "$dest" "$HOME/.local/share/applications"
cp -R "$here/." "$dest/"
sed -e "s|/opt/PrivatePaste|$dest|g" "$here/privatepaste.desktop" > "$HOME/.local/share/applications/privatepaste.desktop"
echo "Installed to $dest. Start it from your application menu, or run $dest/PrivatePaste"
INSTALL
chmod +x "$PKG/install.sh"

TARBALL="$OUT_DIR/PrivatePaste-${VERSION}-${RID}.tar.gz"
tar -C "$WORK" -czf "$TARBALL" PrivatePaste
echo "wrote $TARBALL"
