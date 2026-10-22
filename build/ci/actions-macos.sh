#!/usr/bin/env bash
# Capture the existing Mac build/unit commands under the reusable workflow's installed host lease.
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.." || exit 1
results=${METHODCACHE_CI_RESULTS:-artifacts/ci}
mkdir -p "$results" || exit 1
code=0
for job in build unit; do
  if [ "$job" = unit ] && [ "$code" -ne 0 ]; then
    echo 'skipped: the build failed' > "$results/unit.log"
    echo skipped > "$results/unit.exit"
    continue
  fi
  bash build/ci/jobs.sh "$job" 2>&1 | tee "$results/$job.log"
  # Capture both failures: losing the log must not turn an incomplete run into a pass.
  exits=("${PIPESTATUS[@]}")
  status=${exits[0]}
  [ "$status" -ne 0 ] || status=${exits[1]}
  printf '%s\n' "$status" > "$results/$job.exit"
  [ "$status" -eq 0 ] || code=$status
done
exit "$code"
