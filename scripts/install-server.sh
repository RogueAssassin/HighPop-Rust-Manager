#!/usr/bin/env bash
# Installs the HighPop headless server (HighPop.Server) as a systemd service. Run as root.
#
#   curl -fsSL https://github.com/RogueAssassin/HighPop-Rust-Manager/releases/latest/download/install-server.sh | sudo bash
#   sudo ./install-server.sh [--version vX.Y.Z] [--tarball FILE] [--user NAME] [--no-start] [--uninstall]
#
# Program files go to /opt/highpop-server (replaced on upgrade). Servers, settings and Rust installs
# live in /var/lib/highpop (HIGHPOP_HOME) and are never overwritten or removed by this script.
# The service runs as an unprivileged user (default: highpop), created if it does not exist.
# Rust servers keep running if the service is stopped; only the manager process is restarted.
set -euo pipefail

repo="RogueAssassin/HighPop-Rust-Manager"
prefix="/opt/highpop-server"
state="/var/lib/highpop"
unit="/etc/systemd/system/highpop-server.service"
svc_user="highpop"
version=""; tarball=""; uninstall=0; start=1

usage() { sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)   version="${2:?--version needs a value}"; shift 2 ;;
    --tarball)   tarball="${2:?--tarball needs a file}"; shift 2 ;;
    --user)      svc_user="${2:?--user needs a name}"; shift 2 ;;
    --no-start)  start=0; shift ;;
    --uninstall) uninstall=1; shift ;;
    -h|--help)   usage 0 ;;
    *) echo "Unknown option: $1" >&2; usage 2 ;;
  esac
done

[[ $EUID -eq 0 ]] || { echo "Run this with sudo (it installs a system service)." >&2; exit 1; }
[[ "$svc_user" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] || { echo "Invalid user name: $svc_user" >&2; exit 1; }
command -v systemctl >/dev/null || { echo "systemd is required." >&2; exit 1; }

if [[ $uninstall -eq 1 ]]; then
  systemctl disable --now highpop-server 2>/dev/null || true
  rm -f "$unit" /usr/local/bin/highpop-server
  systemctl daemon-reload
  rm -rf "$prefix"
  echo "The HighPop server service and program files were removed."
  echo "Your data remains in $state (delete it to remove all servers and settings)."
  exit 0
fi

[[ "$(uname -m)" == "x86_64" ]] || { echo "This build is for 64-bit x86 Linux (x86_64)." >&2; exit 1; }
for tool in tar sha256sum; do
  command -v "$tool" >/dev/null || { echo "$tool is required." >&2; exit 1; }
done

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

if [[ -n "$tarball" ]]; then
  [[ -f "$tarball" && -f "$tarball.sha256" ]] || { echo "$tarball and $tarball.sha256 must both exist." >&2; exit 1; }
  archive="$tarball"; expected_file="$tarball.sha256"
else
  command -v curl >/dev/null || { echo "curl is required to download HighPop." >&2; exit 1; }
  if [[ -z "$version" ]]; then
    version="$(curl -fsSL "https://api.github.com/repos/$repo/releases/latest" \
      | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -n 1)"
    [[ -n "$version" ]] || { echo "Could not find the latest HighPop release." >&2; exit 1; }
  fi
  [[ "$version" == v* ]] || version="v$version"
  [[ "$version" =~ ^v[0-9]+(\.[0-9]+){1,3}$ ]] || { echo "Unexpected version: $version" >&2; exit 1; }
  name="HighPop-$version-linux-server-x64.tar.gz"
  base="https://github.com/$repo/releases/download/$version"
  echo "Downloading HighPop server $version..."
  curl -fSL --proto '=https' -o "$work/$name" "$base/$name"
  curl -fSL --proto '=https' -o "$work/$name.sha256" "$base/$name.sha256"
  archive="$work/$name"; expected_file="$work/$name.sha256"
fi

expected="$(awk '{print $1; exit}' "$expected_file")"
actual="$(sha256sum "$archive" | awk '{print $1}')"
if [[ -z "$expected" || "$expected" != "$actual" ]]; then
  echo "Checksum verification failed. Nothing was installed." >&2
  exit 1
fi
echo "Checksum verified."

listing="$(tar -tzf "$archive")"
if grep -qE '(^/|(^|/)\.\.(/|$))' <<<"$listing" || grep -qvE '^HPRM(/|$)' <<<"$listing" \
   || tar -tvzf "$archive" | grep -qE '^[lh]'; then
  echo "The archive has an unexpected layout. Nothing was installed." >&2
  exit 1
fi

if ! id "$svc_user" >/dev/null 2>&1; then
  useradd --system --create-home --home-dir "/home/$svc_user" --shell /usr/sbin/nologin "$svc_user"
  echo "Created service user $svc_user."
fi

mkdir -p "$state"
chown "$svc_user":"$(id -gn "$svc_user")" "$state"
chmod 750 "$state"

was_active=0
systemctl is-active --quiet highpop-server && was_active=1
[[ $was_active -eq 1 ]] && systemctl stop highpop-server

rm -rf "$prefix.new"
mkdir -p "$prefix.new"
tar -xzf "$archive" -C "$prefix.new" --strip-components=1 --no-same-owner --no-same-permissions
chmod 755 "$prefix.new/HighPop.Server"
rm -rf "$prefix.old"
[[ -d "$prefix" ]] && mv "$prefix" "$prefix.old"
mv "$prefix.new" "$prefix"
rm -rf "$prefix.old"
chown -R root:root "$prefix"

cat > "$unit" <<EOF
[Unit]
Description=HighPop headless Rust server manager
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$svc_user
WorkingDirectory=$state
Environment=HIGHPOP_HOME=$state
ExecStart=$prefix/HighPop.Server run
Restart=on-failure
RestartSec=5
# Rust servers keep running if the host stops; only the host process is stopped.
KillMode=process
TimeoutStopSec=30

[Install]
WantedBy=multi-user.target
EOF
chmod 644 "$unit"
systemctl daemon-reload
systemctl enable highpop-server >/dev/null

# A command that always runs the CLI as the service user with the same data folder.
cat > /usr/local/bin/highpop-server <<EOF
#!/bin/sh
if [ "\$(id -u)" = 0 ]; then
  exec runuser -u $svc_user -- env HIGHPOP_HOME=$state $prefix/HighPop.Server "\$@"
elif [ "\$(id -un)" = $svc_user ]; then
  exec env HIGHPOP_HOME=$state $prefix/HighPop.Server "\$@"
else
  exec sudo -u $svc_user env HIGHPOP_HOME=$state $prefix/HighPop.Server "\$@"
fi
EOF
chmod 755 /usr/local/bin/highpop-server

if [[ $start -eq 1 || $was_active -eq 1 ]]; then
  systemctl start highpop-server
  echo "HighPop server started."
else
  echo "HighPop server installed but not started (--no-start)."
fi

cat <<EOF

Installed in $prefix, data in $state, running as $svc_user.
  Status:   systemctl status highpop-server
  Logs:     journalctl -u highpop-server -f
  Commands: highpop-server help   (add, list, install, start, stop, restart)
  Remove:   sudo $0 --uninstall
EOF
