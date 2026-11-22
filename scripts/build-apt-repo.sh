#!/usr/bin/env bash
# Builds a signed apt repository (the static files for GitHub Pages) from HighPop .deb packages.
#
#   APT_GPG_KEY_ID=<fingerprint> scripts/build-apt-repo.sh <deb-dir> <site-dir>
#
# The signing key must already be in the active GnuPG home (no passphrase, as in CI). The site holds:
#   apt/dists/stable/{Release,InRelease,Release.gpg}, apt/dists/stable/main/binary-amd64/Packages(.gz,.xz),
#   apt/pool/main/..., apt/highpop-archive-keyring.gpg (+ .asc) and index.html with the install steps.
set -euo pipefail

debs="${1:?usage: APT_GPG_KEY_ID=<fingerprint> $0 <deb-dir> <site-dir>}"
site="${2:?usage: APT_GPG_KEY_ID=<fingerprint> $0 <deb-dir> <site-dir>}"
key="${APT_GPG_KEY_ID:?set APT_GPG_KEY_ID to the signing key fingerprint}"
base_url="${APT_REPO_URL:-https://rogueassassin.github.io/HighPop-Rust-Manager}"
for tool in apt-ftparchive gpg gzip xz; do command -v "$tool" >/dev/null || { echo "$tool is required (apt-utils, gnupg, xz-utils)." >&2; exit 1; }; done

shopt -s nullglob
packages=("$debs"/highpop-*_amd64.deb)
[[ ${#packages[@]} -gt 0 ]] || { echo "No highpop-*_amd64.deb files in $debs" >&2; exit 1; }

rm -rf "$site"
apt="$site/apt"
mkdir -p "$apt/dists/stable/main/binary-amd64"
for deb in "${packages[@]}"; do
  name="$(basename "$deb")"; pkg="${name%%_*}"
  mkdir -p "$apt/pool/main/${pkg:0:1}/$pkg"
  cp "$deb" "$apt/pool/main/${pkg:0:1}/$pkg/$name"
done

(
  cd "$apt"
  apt-ftparchive packages pool > dists/stable/main/binary-amd64/Packages
  gzip -9nk dists/stable/main/binary-amd64/Packages
  xz -9k dists/stable/main/binary-amd64/Packages
  apt-ftparchive \
    -o APT::FTPArchive::Release::Origin=RogueAssassin \
    -o APT::FTPArchive::Release::Label=HighPop \
    -o APT::FTPArchive::Release::Suite=stable \
    -o APT::FTPArchive::Release::Codename=stable \
    -o APT::FTPArchive::Release::Architectures=amd64 \
    -o APT::FTPArchive::Release::Components=main \
    -o "APT::FTPArchive::Release::Description=HighPop Rust Manager packages" \
    release dists/stable > dists/stable/Release.tmp
  mv dists/stable/Release.tmp dists/stable/Release
  gpg --batch --yes --pinentry-mode loopback -u "$key" --clearsign -o dists/stable/InRelease dists/stable/Release
  gpg --batch --yes --pinentry-mode loopback -u "$key" --armor --detach-sign -o dists/stable/Release.gpg dists/stable/Release
  gpg --batch --yes --export "$key" > highpop-archive-keyring.gpg
  gpg --batch --yes --armor --export "$key" > highpop-archive-keyring.asc
)

touch "$site/.nojekyll"
cat > "$site/index.html" <<HTML
<!doctype html>
<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>HighPop apt repository</title>
<body style="font-family:system-ui;max-width:46rem;margin:2rem auto;padding:0 1rem;line-height:1.5">
<h1>HighPop apt repository</h1>
<p>Signed packages for Ubuntu and Debian (amd64), published by RogueAssassin. Once added, Ubuntu's
Software Updater and <code>apt upgrade</code> keep HighPop current.</p>
<pre>sudo install -d -m 0755 /usr/share/keyrings
curl -fsSL ${base_url}/apt/highpop-archive-keyring.gpg | sudo tee /usr/share/keyrings/highpop-archive-keyring.gpg &gt;/dev/null
echo "deb [signed-by=/usr/share/keyrings/highpop-archive-keyring.gpg] ${base_url}/apt stable main" | sudo tee /etc/apt/sources.list.d/highpop.list
sudo apt update
sudo apt install highpop-desktop    # or: highpop-server</pre>
<p>Source and releases: <a href="https://github.com/RogueAssassin/HighPop-Rust-Manager">github.com/RogueAssassin/HighPop-Rust-Manager</a>.</p>
</body>
HTML
echo "Built apt repository in $site ($(printf '%s ' "${packages[@]##*/}"))"
