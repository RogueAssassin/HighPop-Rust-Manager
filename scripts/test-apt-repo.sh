#!/usr/bin/env bash
# End-to-end test of the apt repository on a disposable Ubuntu/Debian machine (a CI runner): builds debs with a
# throwaway signing key, builds the signed repository, checks the signatures, installs and upgrades through apt,
# and proves a tampered repository is refused. It uses sudo and changes the system, so it needs CI=true or --yes.
#
#   scripts/test-apt-repo.sh [--yes]
set -euo pipefail

[[ "${CI:-}" == "true" || "${1:-}" == "--yes" ]] || { echo "Refusing to change this machine; pass --yes on a disposable host." >&2; exit 1; }
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"; chmod 755 "$work"   # apt runs as the _apt user and must be able to read the test repository and keyring
trap 'rm -rf "$work"' EXIT
pass() { echo "ok: $*"; }
fail() { echo "FAIL: $*" >&2; exit 1; }

export GNUPGHOME="$work/gnupg"; mkdir -m 700 "$GNUPGHOME"
gpg --batch --pinentry-mode loopback --passphrase '' --quick-generate-key "HighPop Test <test@example.invalid>" rsa3072 sign never 2>/dev/null
key="$(gpg --batch --list-secret-keys --with-colons | awk -F: '/^fpr:/ {print $10; exit}')"
gpg --batch --export "$key" > "$work/keyring.gpg"
export APT_GPG_KEY_ID="$key"

mkdir -p "$work/pub/desktop" "$work/pub/server" "$work/debs"
printf '#!/bin/sh\nexit 0\n' > "$work/pub/desktop/HighPop.Desktop"; echo x > "$work/pub/desktop/HighPop.Desktop.dll"
printf '#!/bin/sh\nexit 0\n' > "$work/pub/server/HighPop.Server";  echo x > "$work/pub/server/HighPop.Server.dll"
build_debs() {  # $1 = version
  HIGHPOP_APT_KEYRING="$work/keyring.gpg" "$repo_root/scripts/build-linux-deb.sh" "$work/pub/desktop" "$work/debs" "$1" desktop >/dev/null
  HIGHPOP_APT_KEYRING="$work/keyring.gpg" "$repo_root/scripts/build-linux-deb.sh" "$work/pub/server" "$work/debs" "$1" server >/dev/null
}
build_debs 1.0.0
pass "debs built with the keyring"
listing="$(dpkg-deb -c "$work/debs/highpop-desktop_1.0.0_amd64.deb")"
grep -q "apt/highpop-archive-keyring.gpg" <<<"$listing" && pass "keyring is inside the desktop deb" || fail "no keyring in the desktop deb"
dpkg-deb -e "$work/debs/highpop-server_1.0.0_amd64.deb" "$work/ctl" && grep -q 'highpop.list' "$work/ctl/postinst" && ! grep -q '#APT#' "$work/ctl/postinst" \
  && sh -n "$work/ctl/postinst" && sh -n "$work/ctl/postrm" && pass "server maintainer scripts are valid" || fail "server maintainer scripts are wrong"

"$repo_root/scripts/build-apt-repo.sh" "$work/debs" "$work/site" >/dev/null
site="$work/site/apt"
gpgv --keyring "$work/keyring.gpg" "$site/dists/stable/InRelease" 2>/dev/null && pass "InRelease signature verifies" || fail "InRelease signature"
gpgv --keyring "$work/keyring.gpg" "$site/dists/stable/Release.gpg" "$site/dists/stable/Release" 2>/dev/null && pass "Release.gpg signature verifies" || fail "Release.gpg signature"
[[ -s "$site/highpop-archive-keyring.gpg" && -s "$work/site/index.html" ]] && pass "public key and index page published" || fail "site files missing"

sources="$work/test.list"
echo "deb [signed-by=$work/keyring.gpg] file://$site stable main" > "$sources"
apt_update() { sudo apt-get update -o Dir::Etc::sourcelist="$sources" -o Dir::Etc::sourceparts=- -o APT::Get::List-Cleanup=0 >"$work/update.log" 2>&1; }
apt_update || { cat "$work/update.log" >&2; fail "apt update on the signed repository"; }
pass "apt update accepts the signed repository"
policy="$(apt-cache -o Dir::Etc::sourcelist="$sources" -o Dir::Etc::sourceparts=- policy highpop-desktop)"
grep -q "Candidate: 1.0.0" <<<"$policy" && pass "apt sees highpop-desktop 1.0.0" || fail "package not visible to apt"

sudo apt-get install -y -o Dir::Etc::sourcelist="$sources" -o Dir::Etc::sourceparts=- highpop-desktop highpop-server >/dev/null
[[ -f /etc/apt/sources.list.d/highpop.list && -f /usr/share/keyrings/highpop-archive-keyring.gpg ]] && pass "install registered the HighPop apt source" || fail "apt source was not registered"
grep -q 'signed-by=/usr/share/keyrings/highpop-archive-keyring.gpg' /etc/apt/sources.list.d/highpop.list && pass "the source pins the keyring" || fail "source does not pin the keyring"

build_debs 1.0.1
"$repo_root/scripts/build-apt-repo.sh" "$work/debs" "$work/site" >/dev/null
apt_update || { cat "$work/update.log" >&2; fail "apt update after publishing 1.0.1"; }
upgradable="$(apt list -o Dir::Etc::sourcelist="$sources" -o Dir::Etc::sourceparts=- --upgradable 2>/dev/null)"
grep -q "highpop-desktop.*1.0.1" <<<"$upgradable" && pass "apt offers the 1.0.1 upgrade" || fail "upgrade not offered"
sudo apt-get install -y --only-upgrade -o Dir::Etc::sourcelist="$sources" -o Dir::Etc::sourceparts=- highpop-desktop highpop-server >/dev/null
[[ "$(dpkg-query -W -f='${Version}' highpop-desktop)" == 1.0.1 ]] && pass "upgraded to 1.0.1 through apt" || fail "upgrade did not apply"

# Change the signed Release text after signing (apt falls back to Release + Release.gpg, so change both): apt must refuse it.
sed -i 's/^Origin: RogueAssassin$/Origin: Mallory/' "$site/dists/stable/InRelease" "$site/dists/stable/Release"
grep -q '^Origin: Mallory' "$site/dists/stable/InRelease" || fail "the tamper step did not change the file"
apt_update && rc=0 || rc=$?
echo "--- apt update on the tampered repository (exit $rc) ---"; cat "$work/update.log"; echo "--- end ---"
[[ $rc -ne 0 ]] || fail "a tampered repository was accepted"
pass "a tampered repository is refused"

sudo apt-get purge -y highpop-desktop highpop-server >/dev/null
[[ ! -e /etc/apt/sources.list.d/highpop.list ]] && pass "purge removed the HighPop apt source" || fail "apt source left behind after purge"
echo "apt repository test passed."
