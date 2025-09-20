#!/bin/bash
# Local Benchmark Tracking Script (Bash version)

set -e

RESULTS_DIR=".benchmark-results"
HISTORY_FILE="$RESULTS_DIR/history.json"
LATEST_FILE="$RESULTS_DIR/latest.json"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

initialize_benchmark_dir() {
    if [ ! -d "$RESULTS_DIR" ]; then
        mkdir -p "$RESULTS_DIR"
        echo '{"version":"1.0.0","runs":[]}' > "$HISTORY_FILE"
    fi
}

run_benchmarks() {
    local category="${1:-basic}"

    echo -e "${CYAN}🚀 Running benchmarks with category: $category${NC}"

    local timestamp=$(date +"%Y-%m-%d_%H-%M-%S")
    local commit_hash=$(git rev-parse --short HEAD)
    local branch=$(git branch --show-current)
    local artifacts_dir="../$RESULTS_DIR/artifacts-$timestamp"

    # Create artifacts directory
    mkdir -p "$artifacts_dir"

    # Run benchmarks
    cd MethodCache.Benchmarks

    # Set environment variable for BenchmarkDotNet to export to our directory
    export BenchmarkDotNet_ArtifactsPath="$artifacts_dir"
    export BENCHMARK_QUICK="true"

    dotnet run -c Release -- "$category"

    cd ..

    # Find JSON result - check both custom artifacts path and default BenchmarkDotNet location
    local json_file=$(find "$RESULTS_DIR/artifacts-$timestamp" -name "*-report-full.json" -type f 2>/dev/null | head -1)

    if [ -z "$json_file" ]; then
        # Fallback to default BenchmarkDotNet location
        json_file=$(find "MethodCache.Benchmarks/BenchmarkDotNet.Artifacts" -name "*-report-full.json" -type f 2>/dev/null | head -1)
    fi

    if [ -n "$json_file" ]; then
        # Process results with Python
        python3 - <<EOF
import json
import os
from datetime import datetime

with open('$json_file', 'r') as f:
    results = json.load(f)

summary = {
    'timestamp': '$timestamp',
    'commit': '$commit_hash',
    'branch': '$branch',
    'benchmarks': []
}

for benchmark in results.get('Benchmarks', []):
    stats = benchmark.get('Statistics', {})
    memory = benchmark.get('Memory', {})

    summary['benchmarks'].append({
        'name': benchmark.get('Method', 'Unknown'),
        'mean': stats.get('Mean', 0),
        'error': stats.get('StdErr', 0),
        'allocated': memory.get('BytesAllocatedPerOperation', 0),
        'gen0': memory.get('Gen0CollectionsPerOperation', 0)
    })

# Save as latest
with open('$LATEST_FILE', 'w') as f:
    json.dump(summary, f, indent=2)

# Add to history
with open('$HISTORY_FILE', 'r') as f:
    history = json.load(f)

history['runs'].append(summary)

# Keep only last 50 runs
if len(history['runs']) > 50:
    history['runs'] = history['runs'][-50:]

with open('$HISTORY_FILE', 'w') as f:
    json.dump(history, f, indent=2)

# Print summary
print("\n📊 Performance Summary")
print("═" * 60)
for bench in summary['benchmarks']:
    print(f"{bench['name']:<30} {bench['mean']:>12.2f} ns  {bench['allocated']:>10.0f} B")
EOF

        echo -e "${GREEN}✅ Benchmark complete! Results saved to $RESULTS_DIR${NC}"
    else
        echo -e "${RED}❌ No benchmark results found${NC}"
        exit 1
    fi
}

compare_with_baseline() {
    local baseline="${1:-previous}"

    if [ ! -f "$LATEST_FILE" ]; then
        echo -e "${RED}❌ No latest results found. Run benchmarks first.${NC}"
        exit 1
    fi

    python3 - <<EOF
import json
import sys

with open('$LATEST_FILE', 'r') as f:
    latest = json.load(f)

baseline = None
baseline_source = '$baseline'

if baseline_source == 'previous':
    with open('$HISTORY_FILE', 'r') as f:
        history = json.load(f)
    if len(history['runs']) >= 2:
        baseline = history['runs'][-2]
elif baseline_source and baseline_source != '':
    try:
        with open(baseline_source, 'r') as f:
            baseline = json.load(f)
    except:
        pass

if not baseline:
    print("❌ Baseline not found")
    sys.exit(1)

print("\n📈 Performance Comparison")
print("═" * 70)
print(f"{'Benchmark':<30} {'Baseline':>15} {'Current':>15} {'Change':>10}")
print("─" * 70)

regressions = 0
improvements = 0

for current in latest['benchmarks']:
    base = next((b for b in baseline['benchmarks'] if b['name'] == current['name']), None)

    if base:
        change_pct = ((current['mean'] - base['mean']) / base['mean']) * 100

        if change_pct > 5:
            symbol = "⬆"
            color = "\033[0;31m"  # Red
            regressions += 1
        elif change_pct < -5:
            symbol = "⬇"
            color = "\033[0;32m"  # Green
            improvements += 1
        else:
            symbol = "→"
            color = "\033[0m"  # Normal

        print(f"{current['name']:<30} {base['mean']:>12.2f} ns {current['mean']:>12.2f} ns {color}{symbol} {change_pct:+6.1f}%\033[0m")

print(f"\n📊 Summary: ", end="")
if regressions > 0:
    print(f"\033[0;31m{regressions} regressions\033[0m ", end="")
if improvements > 0:
    print(f"\033[0;32m{improvements} improvements\033[0m ", end="")
if regressions == 0 and improvements == 0:
    print("No significant changes", end="")
print()
EOF
}

update_readme() {
    if [ ! -f "$LATEST_FILE" ]; then
        echo -e "${RED}❌ No benchmark results found${NC}"
        exit 1
    fi

    python3 - <<EOF
import json
import re

with open('$LATEST_FILE', 'r') as f:
    latest = json.load(f)

# Generate markdown table
table = f"""
## 🚀 Performance Benchmarks

Last updated: {latest['timestamp']} (commit: \`{latest['commit']}\`)

| Benchmark | Mean | Error | Allocated |
|-----------|------|-------|-----------|"""

for benchmark in sorted(latest['benchmarks'], key=lambda x: x['name']):
    table += f"\n| {benchmark['name']} | {benchmark['mean']:.2f} ns | ±{benchmark['error']:.2f} | {benchmark['allocated']:.0f} B |"

# Read current README
with open('README.md', 'r') as f:
    readme = f.read()

# Replace or append performance section
pattern = r'## 🚀 Performance Benchmarks.*?(?=##|$)'
if re.search(pattern, readme, re.DOTALL):
    readme = re.sub(pattern, table + "\n\n", readme, flags=re.DOTALL)
elif "## License" in readme:
    readme = readme.replace("## License", table + "\n\n## License")
else:
    readme += "\n\n" + table

with open('README.md', 'w') as f:
    f.write(readme)

print("✅ README.md updated with latest benchmark results")
EOF

    echo -e "${GREEN}✅ README updated${NC}"
}

show_history() {
    python3 - <<EOF
import json

with open('$HISTORY_FILE', 'r') as f:
    history = json.load(f)

print(f"📊 Benchmark History ({len(history['runs'])} runs)")
for run in history['runs'][-10:]:
    print(f"{run['timestamp']} - {run['commit']} ({run['branch']})")
EOF
}

show_usage() {
    echo "Usage: ./benchmark-tracker.sh [command] [options]"
    echo "Commands:"
    echo "  run [category]   - Run benchmarks (basic, providers, concurrent, memory, realworld, generic, serialization, all)"
    echo "  compare [file]   - Compare with baseline (default: previous)"
    echo "  update-readme    - Update README with latest results"
    echo "  history          - Show benchmark history"
    echo ""
    echo "Examples:"
    echo "  ./benchmark-tracker.sh run basic"
    echo "  ./benchmark-tracker.sh run all --update-readme"
    echo "  ./benchmark-tracker.sh compare previous"
}

# Main script
initialize_benchmark_dir

case "${1:-run}" in
    run)
        run_benchmarks "${2:-*}"
        if [ "${3:-}" = "--update-readme" ]; then
            update_readme
        fi
        ;;
    compare)
        compare_with_baseline "${2:-previous}"
        ;;
    update-readme)
        update_readme
        ;;
    history)
        show_history
        ;;
    *)
        show_usage
        ;;
esac