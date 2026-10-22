#!/usr/bin/env bash
# Actions adapter; the existing linux.sh/jobs.sh remain the source of build/test commands.
# All admission happens on Nimbus, across repositories and the external coordinators.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

suite="${METHODCACHE_ACTIONS_SUITE:-${1:-smoke}}"
case "$suite" in
  smoke) jobs=(build unit) ;;
  full) jobs=() ;;
  *) echo 'Expected suite smoke or full' >&2; exit 64 ;;
esac

capacity_lock="${METHODCACHE_ACTIONS_CAPACITY_LOCK:-/srv/skribera-ci/ci-build-capacity.lock}"
# Read-only open: the dedicated runner user must not modify another project's lock file.
# flock(2) supports an exclusive hold on this descriptor. A missing lock is an error.
exec 9< "$capacity_lock"
echo "Waiting for exclusive shared-host capacity: $capacity_lock"
flock --exclusive --timeout 1800 9

# The outer exclusive lease owns admission; a nested shared flock would deadlock.
unset METHODCACHE_CI_SHARED_LOCK
export METHODCACHE_CI_RUN_ID="actions-${GITHUB_RUN_ID:-local}-attempt-${GITHUB_RUN_ATTEMPT:-1}"
export METHODCACHE_CI_DOCKER_ARGS='--cpus=4 --memory=16g'
code=0
timeout --signal=TERM --kill-after=30s 3600 nice -n 10 bash build/ci/linux.sh ${jobs[@]+"${jobs[@]}"} || code=$?

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  {
    printf '### MethodCache Linux pilot\n\nCommit: %s\n\nSuite: %s — exit %s\n\n' \
      "${GITHUB_SHA:-local}" "$suite" "$code"
    printf '| Job | Exit |\n| --- | --- |\n'
    for result in artifacts/ci/*.exit; do
      [ -f "$result" ] || continue
      printf '| %s | %s |\n' "$(basename "$result" .exit)" "$(cat "$result")"
    done
  } >> "$GITHUB_STEP_SUMMARY"
fi
exit "$code"
