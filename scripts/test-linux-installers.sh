#!/usr/bin/env bash
# Installs and removes every Linux package for real, on a disposable Linux machine (a CI runner).
# It uses sudo and changes the system, so it refuses to run unless CI=true or --yes is given.
#
#   scripts/test-linux-installers.sh <package-dir> [--yes]
set -euo pipefail

dir="${1:?usage: $0 <package-dir> [--yes]}"
[[ "${CI:-}" == "true" || "${2:-}" == "--yes" ]] || { echo "Refusing to change this machine; pass --yes on a disposable host." >&2; exit 1; }
dir="$(cd "$dir" && pwd)"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$repo_root/Directory.Build.props" | head -n 1)"

pass() { echo "ok: $*"; }
fail() { echo "FAIL: $*" >&2; exit 1; }
check() { local what="$1"; shift; "$@" >/dev/null 2>&1 && pass "$what" || fail "$what"; }

echo "== .deb packages =="
sudo dpkg -i "$dir/highpop-desktop_${version}_amd64.deb" "$dir/highpop-server_${version}_amd64.deb"
check "desktop binary installed"         test -x /opt/highpop-desktop/HighPop.Desktop
check "desktop command symlink"          test -L /usr/bin/highpop-desktop
check "menu entry installed"             test -f /usr/share/applications/highpop-desktop.desktop
check "menu entry names the binary"      grep -q '^Exec=/opt/highpop-desktop/HighPop.Desktop' /usr/share/applications/highpop-desktop.desktop
check "icon installed"                   test -f /usr/share/icons/hicolor/256x256/apps/highpop-desktop.png
check "server binary installed"          test -x /opt/highpop-server/HighPop.Server
check "service user created"             getent passwd highpop
check "service unit installed"           test -f /lib/systemd/system/highpop-server.service
check "service enabled"                  systemctl is-enabled highpop-server
check "state folder owned by service"    test "$(stat -c %U /var/lib/highpop)" = highpop

echo "== headless server as the service user =="
check "CLI wrapper lists servers"        sudo highpop-server list
check "data created in the state folder" sudo test -d /var/lib/highpop/assets/data
sudo systemctl start highpop-server
sleep 6
if ! systemctl is-active --quiet highpop-server; then
  sudo journalctl -u highpop-server --no-pager -n 40 || true
  fail "service did not stay running"
fi
pass "service running"
sudo systemctl stop highpop-server
check "nothing written beside the program" test ! -e /opt/highpop-server/assets

sudo apt-get remove -y highpop-desktop highpop-server >/dev/null
check "desktop package removed"          test ! -e /opt/highpop-desktop/HighPop.Desktop
check "server package removed"           test ! -e /opt/highpop-server/HighPop.Server
check "data kept after removal"          sudo test -d /var/lib/highpop/assets/data

echo "== install-desktop.sh (user install) =="
home="$(mktemp -d)"
HOME="$home" XDG_DATA_HOME="" bash "$repo_root/scripts/install-desktop.sh" --tarball "$dir/HighPop-v${version}-linux-x64.tar.gz"
check "user install binary"              test -x "$home/.local/share/highpop-desktop/HighPop.Desktop"
check "user menu entry"                  test -f "$home/.local/share/applications/highpop-desktop.desktop"
check "user command"                     test -x "$home/.local/bin/highpop-desktop"
mkdir -p "$home/.local/share/highpop-desktop/assets/data" && echo keep > "$home/.local/share/highpop-desktop/assets/data/x"
HOME="$home" XDG_DATA_HOME="" bash "$repo_root/scripts/install-desktop.sh" --uninstall
check "user uninstall removes program"   test ! -e "$home/.local/share/highpop-desktop/HighPop.Desktop"
check "user uninstall keeps data"        test -f "$home/.local/share/highpop-desktop/assets/data/x"
echo "-- a tampered archive is refused --"
cp "$dir/HighPop-v${version}-linux-x64.tar.gz" "$home/bad.tar.gz"
printf '0000000000000000000000000000000000000000000000000000000000000000  bad.tar.gz\n' > "$home/bad.tar.gz.sha256"
if HOME="$home" XDG_DATA_HOME="" bash "$repo_root/scripts/install-desktop.sh" --tarball "$home/bad.tar.gz" >/dev/null 2>&1; then
  fail "installer accepted a wrong checksum"
fi
pass "wrong checksum refused"

echo "== install-server.sh (systemd) =="
sudo bash "$repo_root/scripts/install-server.sh" --tarball "$dir/HighPop-v${version}-linux-server-x64.tar.gz" --no-start
check "script install binary"            test -x /opt/highpop-server/HighPop.Server
check "script install unit"              test -f /etc/systemd/system/highpop-server.service
check "script install CLI wrapper"       test -x /usr/local/bin/highpop-server
sudo systemctl start highpop-server
sleep 6
systemctl is-active --quiet highpop-server || { sudo journalctl -u highpop-server --no-pager -n 40 || true; fail "script-installed service did not stay running"; }
pass "script-installed service running"
sudo bash "$repo_root/scripts/install-server.sh" --uninstall
check "script uninstall removes program" test ! -e /opt/highpop-server/HighPop.Server
check "script uninstall removes unit"    test ! -e /etc/systemd/system/highpop-server.service

echo "== AppImage =="
appimage="$dir/HighPop-v${version}-x86_64.AppImage"
check "AppImage is executable"           test -x "$appimage"
tmp="$(mktemp -d)"
(cd "$tmp" && "$appimage" --appimage-extract >/dev/null)
check "AppImage holds the app"           test -x "$tmp/squashfs-root/usr/lib/highpop/HighPop.Desktop"
check "AppImage entry point"             test -x "$tmp/squashfs-root/AppRun"
check "AppImage desktop file"            grep -q '^Exec=HighPop.Desktop' "$tmp/squashfs-root/highpop.desktop"

echo "All installer checks passed."
