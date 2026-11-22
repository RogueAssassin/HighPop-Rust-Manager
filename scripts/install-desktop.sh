#!/usr/bin/env bash
# Installs the HighPop Linux desktop app for the current user (no root needed).
#
#   curl -fsSL https://github.com/RogueAssassin/HighPop-Rust-Manager/releases/latest/download/install-desktop.sh | bash
#   ./install-desktop.sh [--version vX.Y.Z] [--tarball FILE] [--prefix DIR] [--uninstall]
#
# It downloads HighPop-vX.Y.Z-linux-x64.tar.gz from the GitHub release, checks its SHA-256 against the
# .sha256 published beside it, unpacks it to ~/.local/share/highpop-desktop, and adds a launcher entry
# and a `highpop-desktop` command. Your servers and settings live in ~/.local/share/highpop-desktop
# (portable layout) and are never overwritten by an upgrade.
set -euo pipefail

repo="RogueAssassin/HighPop-Rust-Manager"
data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
prefix="$data_home/highpop-desktop"
bin_dir="$HOME/.local/bin"
apps_dir="$data_home/applications"
icon_dir="$data_home/icons/hicolor/256x256/apps"
version=""; tarball=""; uninstall=0

usage() { sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)   version="${2:?--version needs a value}"; shift 2 ;;
    --tarball)   tarball="${2:?--tarball needs a file}"; shift 2 ;;
    --prefix)    prefix="${2:?--prefix needs a folder}"; shift 2 ;;
    --uninstall) uninstall=1; shift ;;
    -h|--help)   usage 0 ;;
    *) echo "Unknown option: $1" >&2; usage 2 ;;
  esac
done

if [[ $EUID -eq 0 ]]; then
  echo "Run this as your normal user, not root. For a system-wide install use the .deb package." >&2
  exit 1
fi

if [[ $uninstall -eq 1 ]]; then
  rm -f "$apps_dir/highpop-desktop.desktop" "$icon_dir/highpop-desktop.png" "$bin_dir/highpop-desktop"
  # Remove only the program files, so servers and settings survive.
  if [[ -d "$prefix" ]]; then
    find "$prefix" -mindepth 1 -maxdepth 1 \
      ! -name assets ! -name Servers -exec rm -rf {} +
  fi
  echo "HighPop was removed. Your servers and settings remain in $prefix (delete that folder to remove them)."
  exit 0
fi

[[ "$(uname -m)" == "x86_64" ]] || { echo "This build is for 64-bit x86 Linux (x86_64)." >&2; exit 1; }
for tool in tar sha256sum; do
  command -v "$tool" >/dev/null || { echo "$tool is required." >&2; exit 1; }
done

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

if [[ -n "$tarball" ]]; then
  [[ -f "$tarball" ]] || { echo "$tarball was not found." >&2; exit 1; }
  archive="$tarball"
  [[ -f "$tarball.sha256" ]] || { echo "$tarball.sha256 must sit beside the archive." >&2; exit 1; }
  expected_file="$tarball.sha256"
else
  command -v curl >/dev/null || { echo "curl is required to download HighPop." >&2; exit 1; }
  if [[ -z "$version" ]]; then
    version="$(curl -fsSL "https://api.github.com/repos/$repo/releases/latest" \
      | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -n 1)"
    [[ -n "$version" ]] || { echo "Could not find the latest HighPop release." >&2; exit 1; }
  fi
  [[ "$version" == v* ]] || version="v$version"
  [[ "$version" =~ ^v[0-9]+(\.[0-9]+){1,3}$ ]] || { echo "Unexpected version: $version" >&2; exit 1; }
  name="HighPop-$version-linux-x64.tar.gz"
  base="https://github.com/$repo/releases/download/$version"
  echo "Downloading HighPop $version..."
  curl -fSL --proto '=https' -o "$work/$name" "$base/$name"
  curl -fSL --proto '=https' -o "$work/$name.sha256" "$base/$name.sha256"
  archive="$work/$name"
  expected_file="$work/$name.sha256"
fi

expected="$(awk '{print $1; exit}' "$expected_file")"
actual="$(sha256sum "$archive" | awk '{print $1}')"
if [[ -z "$expected" || "$expected" != "$actual" ]]; then
  echo "Checksum verification failed. Nothing was installed." >&2
  exit 1
fi
echo "Checksum verified."

# Refuse archives that escape the HPRM/ folder or carry links.
listing="$(tar -tzf "$archive")"
if grep -qE '(^/|(^|/)\.\.(/|$))' <<<"$listing" || grep -qvE '^HPRM(/|$)' <<<"$listing" \
   || tar -tvzf "$archive" | grep -qE '^[lh]'; then
  echo "The archive has an unexpected layout. Nothing was installed." >&2
  exit 1
fi

mkdir -p "$prefix" "$bin_dir" "$apps_dir" "$icon_dir"
if pgrep -f "$prefix/HighPop.Desktop" >/dev/null 2>&1; then
  echo "HighPop is running from $prefix. Close it and run this again." >&2
  exit 1
fi
tar -xzf "$archive" -C "$prefix" --strip-components=1 --no-same-owner --no-same-permissions
chmod 755 "$prefix/HighPop.Desktop"

if [[ -f "$prefix/highpop.png" ]]; then
  cp "$prefix/highpop.png" "$icon_dir/highpop-desktop.png"
fi

cat > "$bin_dir/highpop-desktop" <<EOF
#!/usr/bin/env bash
exec "$prefix/HighPop.Desktop" "\$@"
EOF
chmod 755 "$bin_dir/highpop-desktop"

cat > "$apps_dir/highpop-desktop.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=HighPop Rust Manager
Comment=Manage Rust and other game servers
Exec="$prefix/HighPop.Desktop"
Icon=highpop-desktop
Terminal=false
Categories=Game;Utility;
StartupWMClass=HighPop.Desktop
EOF

command -v update-desktop-database >/dev/null && update-desktop-database "$apps_dir" >/dev/null 2>&1 || true

echo
echo "HighPop is installed in $prefix"
echo "Start it from your applications menu, or run: highpop-desktop"
case ":$PATH:" in *":$bin_dir:"*) ;; *) echo "Note: $bin_dir is not on your PATH; use the menu entry or $bin_dir/highpop-desktop." ;; esac
echo "To remove it later: $0 --uninstall"
