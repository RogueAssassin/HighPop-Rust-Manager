#!/usr/bin/env bash
# Runs the Linux self-update end to end on a Linux host: installs an OLD desktop tarball, serves a NEW one
# over local HTTP, and drives the real updater (HighPop.LinuxCheck --selfupdate-apply), then checks the result.
#
#   scripts/test-linux-update.sh <old.tar.gz> <new.tar.gz>     (the LinuxCheck project must already be built, Release)
#
# Checks: program files equal the new package, CHANGELOG.md is the new one, user data under assets/ survives,
# and no staging files are left behind. It does not cover the rollback path or the real GUI relaunch.
set -euo pipefail

old="$(realpath "${1:?usage: $0 <old.tar.gz> <new.tar.gz>}")"
new="$(realpath "${2:?usage: $0 <old.tar.gz> <new.tar.gz>}")"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
port=18765
work="$(mktemp -d)"
server_pid=""
cleanup() { [[ -n "$server_pid" ]] && kill "$server_pid" 2>/dev/null || true; rm -rf "$work"; }
trap cleanup EXIT

pass() { echo "ok: $*"; }
fail() { echo "FAIL: $*" >&2; exit 1; }

mkdir -p "$work/app" "$work/new" "$work/serve"
tar -xzf "$old" -C "$work/app" --strip-components=1
tar -xzf "$new" -C "$work/new" --strip-components=1
[[ -x "$work/app/HighPop.Desktop" ]] || fail "old package has no HighPop.Desktop"

# User data that an update must never touch.
mkdir -p "$work/app/assets/data/servers"
echo '{"keep":"me"}' > "$work/app/assets/data/servers/servers.json"
echo custom > "$work/app/assets/custom.txt"

new_name="$(basename "$new")"
cp "$new" "$new.sha256" "$work/serve/"
(cd "$work/serve" && python3 -m http.server "$port" --bind 127.0.0.1 >/dev/null 2>&1) &
server_pid=$!
for _ in $(seq 1 20); do curl -fsS "http://127.0.0.1:$port/$new_name.sha256" >/dev/null 2>&1 && break; sleep 0.5; done

dotnet run --no-build -c Release --project "$repo_root/HighPop.LinuxCheck/HighPop.LinuxCheck.csproj" -- \
  --selfupdate-apply "http://127.0.0.1:$port/$new_name" "$work/app" || fail "updater did not stage and start the swap"

# The swap script deletes itself as its last step but one; wait for that, then for the relaunch to settle.
script="$work/app/assets/data/update/_apply_update.sh"
for _ in $(seq 1 60); do [[ -e "$script" ]] || break; sleep 0.5; done
[[ ! -e "$script" ]] || fail "the swap script did not finish"
sleep 2

if diff -rq --exclude=assets "$work/app" "$work/new" >"$work/diff.txt"; then pass "program files equal the new package"
else cat "$work/diff.txt" >&2; fail "program files differ from the new package"; fi

[[ "$(grep -m1 '^## ' "$work/app/CHANGELOG.md")" == "$(grep -m1 '^## ' "$work/new/CHANGELOG.md")" ]] \
  && pass "CHANGELOG.md is the new one" || fail "CHANGELOG.md was not refreshed"
[[ "$(cat "$work/app/assets/data/servers/servers.json")" == '{"keep":"me"}' && "$(cat "$work/app/assets/custom.txt")" == custom ]] \
  && pass "user data under assets/ was kept" || fail "user data was changed"
[[ ! -e "$work/app/assets/data/update/_staged" && ! -e "$work/app/assets/data/update/_old" ]] \
  && pass "no staging files left" || fail "staging files were left behind"
echo "Linux update test passed."
