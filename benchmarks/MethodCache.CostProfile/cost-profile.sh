#!/usr/bin/env bash
# Builds the cost profile at a base revision and at the working tree, then compares them.
#
#   benchmarks/MethodCache.CostProfile/cost-profile.sh <base-ref> [case filter]
#       Allocations and Redis/SQL commands per operation for both, and the gate (compare.py).
#   benchmarks/MethodCache.CostProfile/cost-profile.sh --time <pairs> <base-ref> [case filter]
#       Time per operation: each case in its own process, <pairs> times per side, alternating which side goes
#       first (paired.py). About 6 seconds per case and launch, so filter it; run it on a quiet machine.
#
# Redis and SQL Server cases need METHODCACHE_REDIS_URL and METHODCACHE_SQLSERVER_CONNECTION. Results and the
# report go to artifacts/cost-profile (COST_PROFILE_OUT).
set -euo pipefail

pairs=0
if [ "${1:-}" = --time ]; then
  pairs=${2:?usage: cost-profile.sh --time <pairs> <base-ref> [case filter]}
  shift 2
fi
base_ref=${1:?usage: cost-profile.sh [--time <pairs>] <base-ref> [case filter]}
filter=${2:-}
root=$(git rev-parse --show-toplevel)
here="$root/benchmarks/MethodCache.CostProfile"
out=${COST_PROFILE_OUT:-$root/artifacts/cost-profile}
mkdir -p "$out"
# A rerun must use a new directory: failed/noisy studies are evidence too.
for previous in "$out"/base-*.jsonl "$out"/head-*.jsonl "$out"/base.jsonl "$out"/head.jsonl "$out"/launch.jsonl; do
  if [ -e "$previous" ]; then
    echo "COST_PROFILE_OUT already contains results; choose a fresh directory: $out" >&2
    exit 1
  fi
done

# The physical path: on macOS mktemp returns /var/folders, a symlink to /private/var, and restore then loses the
# base's project references
base_tree=$(cd "$(mktemp -d)" && pwd -P)
cleanup() { git -C "$root" worktree remove --force "$base_tree" >/dev/null 2>&1 || rm -rf "$base_tree"; }
trap cleanup EXIT
git -C "$root" worktree add --detach "$base_tree" "$base_ref" >/dev/null

# The base gets this revision's profile and harness, so both sides run exactly the same cases
mkdir -p "$base_tree/benchmarks/MethodCache.CostProfile"
cp "$here"/*.cs "$here"/*.csproj "$base_tree/benchmarks/MethodCache.CostProfile/"
for file in Comparison/GeneratedContractBenchmarks.cs Microbenchmarks/DesignWorkloadBenchmarks.cs Microbenchmarks/HttpResponsePathBenchmarks.cs \
  Microbenchmarks/GeneratedPrefixHitPathBenchmarks.cs Microbenchmarks/PrefixBenchmarkSetup.cs; do
  mkdir -p "$(dirname "$base_tree/benchmarks/MethodCache.Benchmarks/$file")"
  cp "$root/benchmarks/MethodCache.Benchmarks/$file" "$base_tree/benchmarks/MethodCache.Benchmarks/$file"
done

for side in base head; do
  tree=$root
  [ "$side" = base ] && tree=$base_tree
  echo "Building the $side profile"
  dotnet build "$tree/benchmarks/MethodCache.CostProfile/MethodCache.CostProfile.csproj" -c Release -o "$out/$side-bin" \
    -p:NBGV_GitEngine=Disabled -nologo -v:q -clp:ErrorsOnly
done

if [ "$pairs" -gt 0 ]; then
  # One process per case: cases timed in one process shape each other's JIT and GC state. On Windows the
  # names end in CR, which would make every --exact launch miss
  cases=()
  while IFS= read -r case; do
    cases+=("$case")
  done < <(dotnet "$out/head-bin/MethodCache.CostProfile.dll" "$filter" --list | tr -d '\r')
  for i in $(seq 1 "$pairs"); do
    # Alternate which side goes first, so a trend on the machine does not favor one side
    order="base head"
    [ $((i % 2)) -eq 0 ] && order="head base"
    echo "Pair $i/$pairs: timing ${#cases[@]} cases"
    for case in "${cases[@]}"; do
      for side in $order; do
        dotnet "$out/$side-bin/MethodCache.CostProfile.dll" "$case" --exact --time --out "$out/launch.jsonl" >/dev/null
        cat "$out/launch.jsonl" >> "$out/$side-$i.jsonl"
      done
    done
  done
  python3 "$here/paired.py" "$out" --accepted "$here/accepted.json" --markdown "$out/timing.md"
  exit
fi

for side in base head; do
  echo "Profiling $side"
  dotnet "$out/$side-bin/MethodCache.CostProfile.dll" "$filter" --out "$out/$side.jsonl" >/dev/null
done
python3 "$here/compare.py" "$out/base.jsonl" "$out/head.jsonl" --accepted "$here/accepted.json" --markdown "$out/report.md"
