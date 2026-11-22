#!/usr/bin/env bash
# Builds a single-file AppImage of the desktop app from a published Linux build.
#
#   scripts/build-linux-appimage.sh <publish-dir> <output-dir> <version>
#
# Output: HighPop-v<version>-x86_64.AppImage (+ .sha256). Run it with: chmod +x it && ./it
# An AppImage is read-only, so servers and settings are kept in ~/.local/share/highpop and the app
# does not update itself; download a newer AppImage instead.
# Needs FUSE 2 on the machine that runs it (Ubuntu 24.04+: sudo apt install libfuse2t64), or run it
# with --appimage-extract-and-run.
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: $0 <publish-dir> <output-dir> <version>" >&2
  exit 2
fi
publish="$1"; out="$2"; version="$3"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
[[ "$version" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]] || { echo "version must look like 1.2.3 (no leading v)" >&2; exit 2; }
[[ -f "$publish/HighPop.Desktop" && -f "$publish/HighPop.Desktop.dll" ]] || { echo "HighPop.Desktop was not found in $publish" >&2; exit 1; }

# Pinned packaging tool: a different file is refused.
tool_url="https://github.com/AppImage/appimagetool/releases/download/1.9.0/appimagetool-x86_64.AppImage"
tool_sha256="46fdd785094c7f6e545b61afcfb0f3d98d8eab243f644b4b17698c01d06083d1"

command -v desktop-file-validate >/dev/null || { echo "desktop-file-validate is required (sudo apt install desktop-file-utils)." >&2; exit 1; }
mkdir -p "$out"; out="$(cd "$out" && pwd)"
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT

tool="${APPIMAGETOOL:-}"
if [[ -z "$tool" ]]; then
  tool="$work/appimagetool"
  curl -fSL --proto '=https' -o "$tool" "$tool_url"
  chmod +x "$tool"
fi
actual="$(sha256sum "$tool" | cut -d' ' -f1)"
[[ "$actual" == "$tool_sha256" ]] || { echo "appimagetool checksum mismatch; refusing to run it." >&2; exit 1; }

appdir="$work/HighPop.AppDir"
mkdir -p "$appdir/usr/lib/highpop" "$appdir/usr/share/icons/hicolor/256x256/apps"
cp -a "$publish/." "$appdir/usr/lib/highpop/"
find "$appdir/usr/lib/highpop" -name '*.pdb' -delete
cp "$repo_root/CHANGELOG.md" "$appdir/usr/lib/highpop/CHANGELOG.md"
chmod 755 "$appdir/usr/lib/highpop/HighPop.Desktop"

cp "$repo_root/HighPop.Desktop/Assets/highpop-mark.png" "$appdir/highpop.png"
cp "$repo_root/HighPop.Desktop/Assets/highpop-mark.png" "$appdir/usr/share/icons/hicolor/256x256/apps/highpop.png"

cat > "$appdir/highpop.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=HighPop Rust Manager
Comment=Manage Rust and other game servers
Exec=HighPop.Desktop
Icon=highpop
Terminal=false
Categories=Game;Utility;
StartupWMClass=HighPop.Desktop
EOF

cat > "$appdir/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/lib/highpop/HighPop.Desktop" "$@"
EOF
chmod 755 "$appdir/AppRun"

epoch="${SOURCE_DATE_EPOCH:-$(git -C "$repo_root" log -1 --format=%ct 2>/dev/null || date +%s)}"
name="HighPop-v${version}-x86_64.AppImage"
rm -f "$out/$name" "$out/$name.sha256"
# Runs without FUSE, so it also works inside CI containers.
ARCH=x86_64 SOURCE_DATE_EPOCH="$epoch" APPIMAGE_EXTRACT_AND_RUN=1 "$tool" --no-appstream "$appdir" "$out/$name" >/dev/null
chmod 755 "$out/$name"
(cd "$out" && sha256sum "$name" > "$name.sha256")
echo "Wrote $out/$name ($(du -h "$out/$name" | cut -f1))"
