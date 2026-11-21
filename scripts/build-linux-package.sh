#!/usr/bin/env bash
# Packs a published Linux build into HighPop-vX.Y.Z-<kind>.tar.gz with a .sha256 beside it.
#
#   scripts/build-linux-package.sh <publish-dir> <output-dir> <version> <desktop|server> [commit]
#
# desktop -> HighPop-vX.Y.Z-linux-x64.tar.gz         (the app that updates itself; keep this name stable)
# server  -> HighPop-vX.Y.Z-linux-server-x64.tar.gz  (headless host)
#
# Both hold one top-level HPRM/ folder, the layout the desktop self-updater unpacks.
set -euo pipefail

if [[ $# -lt 4 ]]; then
  echo "usage: $0 <publish-dir> <output-dir> <version> <desktop|server> [commit]" >&2
  exit 2
fi

publish="$1"; out="$2"; version="$3"; kind="$4"; commit="${5:-local}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

case "$kind" in
  desktop) base="HighPop-v${version}-linux-x64";        main="HighPop.Desktop" ;;
  server)  base="HighPop-v${version}-linux-server-x64"; main="HighPop.Server" ;;
  *) echo "kind must be desktop or server" >&2; exit 2 ;;
esac

[[ -f "$publish/$main" && -f "$publish/$main.dll" ]] || { echo "$main was not found in $publish" >&2; exit 1; }

mkdir -p "$out"
out="$(cd "$out" && pwd)"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
mkdir "$stage/HPRM"

cp -a "$publish/." "$stage/HPRM/"
find "$stage/HPRM" -name '*.pdb' -delete
cp "$repo_root/CHANGELOG.md" "$stage/HPRM/CHANGELOG.md"
[[ "$kind" == server ]] && cp "$repo_root/scripts/highpop-server.service" "$stage/HPRM/"
echo "$version $commit" > "$stage/HPRM/BUILD.txt"

# Archives built on any host must unpack to the same modes, and never carry links.
if [[ -n "$(find "$stage/HPRM" -type l)" ]]; then
  echo "The publish output contains symbolic links, which the updater refuses" >&2
  exit 1
fi
find "$stage/HPRM" -type d -exec chmod 755 {} +
find "$stage/HPRM" -type f -exec chmod 644 {} +
chmod 755 "$stage/HPRM/$main"

epoch="${SOURCE_DATE_EPOCH:-$(git -C "$repo_root" log -1 --format=%ct 2>/dev/null || date +%s)}"
archive="$out/$base.tar.gz"
rm -f "$archive" "$archive.sha256"
tar --sort=name --owner=0 --group=0 --numeric-owner --mtime="@$epoch" -C "$stage" -czf "$archive" HPRM

# Verify what was written, not what was intended.
listing="$(tar -tzf "$archive")"
for required in "HPRM/$main" "HPRM/$main.dll" "HPRM/CHANGELOG.md"; do
  grep -qxF "$required" <<<"$listing" || { echo "Archive is missing $required" >&2; exit 1; }
done
if grep -qE '(^/|(^|/)\.\.(/|$))' <<<"$listing" || grep -qvE '^HPRM(/|$)' <<<"$listing"; then
  echo "Archive contains paths outside HPRM/" >&2
  exit 1
fi
if tar -tvzf "$archive" | grep -qE '^[lh]'; then
  echo "Archive contains links" >&2
  exit 1
fi

(cd "$out" && sha256sum "$base.tar.gz" > "$base.tar.gz.sha256")
echo "Wrote $archive ($(du -h "$archive" | cut -f1))"
