#!/usr/bin/env bash
# Signs every release file in a folder with the key in the current GNUPGHOME: writes <file>.asc (ASCII-armored detached
# signature) next to each file, skipping .sha256 and .asc files, exports the public key as highpop-release-key.asc, and
# verifies each signature against that exported key before finishing.
#
#   APT_GPG_KEY_ID=<fingerprint> scripts/sign-release-assets.sh <folder>
set -euo pipefail
dir="${1:?usage: sign-release-assets.sh <folder>}"
key="${APT_GPG_KEY_ID:?set APT_GPG_KEY_ID to the signing key fingerprint}"
cd "$dir"
gpg --batch --armor --export "$key" > highpop-release-key.asc
[[ -s highpop-release-key.asc ]] || { echo "could not export the public key" >&2; exit 1; }
verify_home="$(mktemp -d)"; chmod 700 "$verify_home"; trap 'rm -rf "$verify_home"' EXIT
GNUPGHOME="$verify_home" gpg --batch --quiet --import highpop-release-key.asc
count=0
for f in *; do
  [[ -f "$f" ]] || continue
  case "$f" in *.sha256|*.asc) continue ;; esac
  gpg --batch --yes --pinentry-mode loopback --passphrase '' --local-user "$key" --armor --detach-sign --output "$f.asc" "$f"
  GNUPGHOME="$verify_home" gpg --batch --quiet --verify "$f.asc" "$f" 2>/dev/null || { echo "signature check failed for $f" >&2; exit 1; }
  count=$((count + 1))
done
echo "signed and verified $count files; public key: highpop-release-key.asc"
