#!/usr/bin/env bash
# Builds a .deb from a published Linux build.
#
#   scripts/build-linux-deb.sh <publish-dir> <output-dir> <version> <desktop|server>
#
# desktop -> highpop-desktop_<version>_amd64.deb   (/opt/highpop-desktop, menu entry, `highpop-desktop` command)
# server  -> highpop-server_<version>_amd64.deb    (/opt/highpop-server, systemd service, `highpop-server` command)
#
# Installed copies are read-only, so HighPop keeps its servers and settings in ~/.local/share/highpop
# (desktop) or /var/lib/highpop (server) and does not offer to update itself: use apt or a newer .deb.
set -euo pipefail

if [[ $# -ne 4 ]]; then
  echo "usage: $0 <publish-dir> <output-dir> <version> <desktop|server>" >&2
  exit 2
fi
publish="$1"; out="$2"; version="$3"; kind="$4"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
command -v dpkg-deb >/dev/null || { echo "dpkg-deb is required (install the dpkg package)." >&2; exit 1; }
[[ "$version" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]] || { echo "version must look like 1.2.3 (no leading v)" >&2; exit 2; }

case "$kind" in
  desktop) pkg="highpop-desktop"; main="HighPop.Desktop"; dir="/opt/highpop-desktop" ;;
  server)  pkg="highpop-server";  main="HighPop.Server";  dir="/opt/highpop-server" ;;
  *) echo "kind must be desktop or server" >&2; exit 2 ;;
esac
[[ -f "$publish/$main" && -f "$publish/$main.dll" ]] || { echo "$main was not found in $publish" >&2; exit 1; }

mkdir -p "$out"; out="$(cd "$out" && pwd)"
stage="$(mktemp -d)"; trap 'rm -rf "$stage"' EXIT
root="$stage/root"
mkdir -p "$root$dir" "$root/DEBIAN" "$root/usr/bin"

cp -a "$publish/." "$root$dir/"
find "$root$dir" -name '*.pdb' -delete
cp "$repo_root/CHANGELOG.md" "$root$dir/CHANGELOG.md"
find "$root" -type d -exec chmod 755 {} +
find "$root$dir" -type f -exec chmod 644 {} +
chmod 755 "$root$dir/$main"

# Optional: with HIGHPOP_APT_KEYRING (the public signing key) the package also registers the HighPop apt repository
# on a fresh install, so later versions arrive through apt and Ubuntu Software Updater.
apt_url="${HIGHPOP_APT_URL:-https://rogueassassin.github.io/HighPop-Rust-Manager}/apt"
apt_postinst=""; apt_postrm=""
if [[ -n "${HIGHPOP_APT_KEYRING:-}" ]]; then
  [[ -f "$HIGHPOP_APT_KEYRING" ]] || { echo "HIGHPOP_APT_KEYRING does not exist: $HIGHPOP_APT_KEYRING" >&2; exit 1; }
  mkdir -p "$root$dir/apt"
  install -m 644 "$HIGHPOP_APT_KEYRING" "$root$dir/apt/highpop-archive-keyring.gpg"
  IFS= read -r -d '' apt_postinst <<'EOF' || true
  # First install only: add the HighPop apt repository so updates arrive through apt.
  if [ -z "$2" ] && [ -d /etc/apt/sources.list.d ] && [ ! -e /etc/apt/sources.list.d/highpop.list ]; then
    install -d -m 0755 /usr/share/keyrings
    install -m 0644 @DIR@/apt/highpop-archive-keyring.gpg /usr/share/keyrings/highpop-archive-keyring.gpg
    echo "deb [signed-by=/usr/share/keyrings/highpop-archive-keyring.gpg] @URL@ stable main" > /etc/apt/sources.list.d/highpop.list
  fi
EOF
  apt_postinst="${apt_postinst//@DIR@/$dir}"; apt_postinst="${apt_postinst//@URL@/$apt_url}"
  IFS= read -r -d '' apt_postrm <<'EOF' || true
if [ "$1" = purge ] && [ ! -d /opt/highpop-desktop ] && [ ! -d /opt/highpop-server ]; then
  rm -f /etc/apt/sources.list.d/highpop.list /usr/share/keyrings/highpop-archive-keyring.gpg
fi
EOF
fi

if [[ "$kind" == desktop ]]; then
  mkdir -p "$root/usr/share/applications" "$root/usr/share/icons/hicolor/256x256/apps"
  cp "$repo_root/HighPop.Desktop/Assets/highpop-mark.png" "$root/usr/share/icons/hicolor/256x256/apps/highpop-desktop.png"
  chmod 644 "$root/usr/share/icons/hicolor/256x256/apps/highpop-desktop.png"
  cat > "$root/usr/share/applications/highpop-desktop.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=HighPop Rust Manager
Comment=Manage Rust and other game servers
Exec=$dir/$main
Icon=highpop-desktop
Terminal=false
Categories=Game;Utility;
StartupWMClass=HighPop.Desktop
EOF
  chmod 644 "$root/usr/share/applications/highpop-desktop.desktop"
  ln -s "$dir/$main" "$root/usr/bin/highpop-desktop"
  depends="libc6, libx11-6, libice6, libsm6, libfontconfig1"
  description="HighPop Rust Manager (desktop)
 Desktop app for installing, running and administering Rust and other dedicated
 game servers."
else
  mkdir -p "$root/lib/systemd/system"
  cat > "$root/lib/systemd/system/highpop-server.service" <<EOF
[Unit]
Description=HighPop headless Rust server manager
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=highpop
WorkingDirectory=/var/lib/highpop
Environment=HIGHPOP_HOME=/var/lib/highpop
ExecStart=$dir/$main run
Restart=on-failure
RestartSec=5
# Rust servers keep running if the host stops; only the host process is stopped.
KillMode=process
TimeoutStopSec=30

[Install]
WantedBy=multi-user.target
EOF
  chmod 644 "$root/lib/systemd/system/highpop-server.service"
  cat > "$root/usr/bin/highpop-server" <<EOF
#!/bin/sh
# Runs the HighPop CLI as the service user with the service's data folder.
if [ "\$(id -u)" = 0 ]; then
  exec runuser -u highpop -- env HIGHPOP_HOME=/var/lib/highpop $dir/$main "\$@"
elif [ "\$(id -un)" = highpop ]; then
  exec env HIGHPOP_HOME=/var/lib/highpop $dir/$main "\$@"
else
  exec sudo -u highpop env HIGHPOP_HOME=/var/lib/highpop $dir/$main "\$@"
fi
EOF
  chmod 755 "$root/usr/bin/highpop-server"
  depends="libc6, systemd, adduser"
  description="HighPop Rust Manager (headless server)
 Runs HighPop without a window as a systemd service: reattaches to running
 servers, restarts crashed ones and serves the Web API."

  cat > "$root/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
if [ "$1" = configure ]; then
  if ! getent passwd highpop >/dev/null; then
    adduser --system --group --home /home/highpop --shell /usr/sbin/nologin highpop >/dev/null
  fi
  #APT#
  install -d -o highpop -g highpop -m 750 /var/lib/highpop
  systemctl daemon-reload >/dev/null 2>&1 || true
  systemctl enable highpop-server >/dev/null 2>&1 || true
  # Upgrades restart a running service; a fresh install is left stopped until the operator adds a server.
  if systemctl is-active --quiet highpop-server 2>/dev/null; then
    systemctl restart highpop-server || true
  else
    echo "HighPop server installed. Add a server with 'highpop-server add <name>', then 'sudo systemctl start highpop-server'."
  fi
fi
exit 0
EOF
  cat > "$root/DEBIAN/prerm" <<'EOF'
#!/bin/sh
set -e
if [ "$1" = remove ]; then
  systemctl disable --now highpop-server >/dev/null 2>&1 || true
fi
exit 0
EOF
  chmod 755 "$root/DEBIAN/postinst" "$root/DEBIAN/prerm"
fi

# Maintainer scripts for the apt repository registration (the server already has a postinst with an #APT# marker).
if [[ -n "$apt_postinst" ]]; then
  if [[ -f "$root/DEBIAN/postinst" ]]; then
    printf '%s\n' "$apt_postinst" > "$stage/apt_block"
    awk 'FNR==NR { block = block $0 "\n"; next } /^  #APT#$/ { printf "%s", block; next } { print }' \
      "$stage/apt_block" "$root/DEBIAN/postinst" > "$stage/postinst.new"
    mv "$stage/postinst.new" "$root/DEBIAN/postinst"
  else
    printf '#!/bin/sh\nset -e\nif [ "$1" = configure ]; then\n%s\nfi\nexit 0\n' "$apt_postinst" > "$root/DEBIAN/postinst"
  fi
  printf '#!/bin/sh\nset -e\n%s\nexit 0\n' "$apt_postrm" > "$root/DEBIAN/postrm"
  chmod 755 "$root/DEBIAN/postinst" "$root/DEBIAN/postrm"
elif [[ -f "$root/DEBIAN/postinst" ]]; then
  sed -i '/^  #APT#$/d' "$root/DEBIAN/postinst"
fi

size_kb="$(du -sk "$root" | cut -f1)"
cat > "$root/DEBIAN/control" <<EOF
Package: $pkg
Version: $version
Section: games
Priority: optional
Architecture: amd64
Maintainer: RogueAssassin <noreply@users.noreply.github.com>
Depends: $depends
Recommends: libicu-dev | libicu74 | libicu72 | libicu70
Installed-Size: $size_kb
Homepage: https://github.com/RogueAssassin/HighPop-Rust-Manager
Description: $description
EOF

deb="$out/${pkg}_${version}_amd64.deb"
rm -f "$deb" "$deb.sha256"
dpkg-deb --root-owner-group -Zxz --build "$root" "$deb" >/dev/null
(cd "$out" && sha256sum "$(basename "$deb")" > "$(basename "$deb").sha256")
echo "Wrote $deb ($(du -h "$deb" | cut -f1))"
