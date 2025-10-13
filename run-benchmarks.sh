#!/bin/bash

set -e

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

BENCHMARK_CATEGORY="${1:-quick}"
BENCHMARK_PROJECT="MethodCache.Benchmarks"
RESULTS_DIR="BenchmarkDotNet.Artifacts/results"

print_header() {
    echo ""
    echo -e "${BLUE}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
    echo -e "${BLUE}  $1${NC}"
    echo -e "${BLUE}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
    echo ""
}

print_section() {
    echo ""
    echo -e "${GREEN}▶ $1${NC}"
    echo ""
}

print_warning() {
    echo -e "${YELLOW}⚠ $1${NC}"
}

print_error() {
    echo -e "${RED}✗ $1${NC}"
}

print_success() {
    echo -e "${GREEN}✓ $1${NC}"
}

show_help() {
    cat << EOF
${GREEN}MethodCache Benchmark Runner${NC}

${BLUE}Usage:${NC}
  ./run-benchmarks.sh [category] [options]

${BLUE}Categories:${NC}
  ${GREEN}basic${NC}        - Basic caching operations (hit/miss, different data sizes)
  ${GREEN}baseline${NC}     - Compare with Microsoft.Extensions.Caching.Memory and LazyCache
  ${GREEN}providers${NC}    - Compare different cache providers (InMemory, Redis, Hybrid)
  ${GREEN}concurrent${NC}   - Concurrent access and scalability tests
  ${GREEN}memory${NC}       - Memory usage and GC pressure analysis
  ${GREEN}realworld${NC}    - Real-world application scenarios
  ${GREEN}generic${NC}      - Generic interface performance
  ${GREEN}serialization${NC} - Serialization performance comparison
  ${GREEN}quick${NC}        - Quick benchmarks for development (minimal parameters) ${YELLOW}[default]${NC}
  ${GREEN}all${NC}          - Run all benchmark categories

${BLUE}Options:${NC}
  ${GREEN}--quick${NC}      - Run in quick mode (fewer iterations, faster results)
  ${GREEN}--clean${NC}      - Clean before building
  ${GREEN}--skip-build${NC} - Skip the build step (use existing build)
  ${GREEN}--show-latest${NC} - Show the latest benchmark results without running

${BLUE}Examples:${NC}
  ./run-benchmarks.sh quick
  ./run-benchmarks.sh providers --quick
  ./run-benchmarks.sh all --clean
  ./run-benchmarks.sh --show-latest

${BLUE}Results:${NC}
  Results are saved to: ${BENCHMARK_PROJECT}/${RESULTS_DIR}
EOF
}

show_latest_results() {
    print_header "Latest Benchmark Results"

    RESULTS_PATH="${BENCHMARK_PROJECT}/${RESULTS_DIR}"

    if [ ! -d "$RESULTS_PATH" ]; then
        print_error "No results found at $RESULTS_PATH"
        exit 1
    fi

    # Find the most recent markdown result file
    LATEST_MD=$(find "$RESULTS_PATH" -name "*.md" -type f -print0 | xargs -0 ls -t | head -n 1)

    if [ -z "$LATEST_MD" ]; then
        print_warning "No markdown results found"
    else
        print_section "Summary from: $(basename "$LATEST_MD")"
        cat "$LATEST_MD"
    fi

    # Find the most recent JSON result
    LATEST_JSON=$(find "$RESULTS_PATH" -name "*-report-full.json" -type f -print0 | xargs -0 ls -t | head -n 1)

    if [ -n "$LATEST_JSON" ]; then
        echo ""
        print_section "Latest Results Location"
        echo "  Markdown: $LATEST_MD"
        echo "  JSON:     $LATEST_JSON"
    fi
}

# Parse arguments
SKIP_BUILD=false
CLEAN=false
QUICK_MODE=false
SHOW_LATEST=false

for arg in "$@"; do
    case $arg in
        --help|-h)
            show_help
            exit 0
            ;;
        --skip-build)
            SKIP_BUILD=true
            ;;
        --clean)
            CLEAN=true
            ;;
        --quick)
            QUICK_MODE=true
            ;;
        --show-latest)
            SHOW_LATEST=true
            ;;
        --*)
            print_error "Unknown option: $arg"
            show_help
            exit 1
            ;;
        *)
            BENCHMARK_CATEGORY="$arg"
            ;;
    esac
done

# If showing latest, do that and exit
if [ "$SHOW_LATEST" = true ]; then
    show_latest_results
    exit 0
fi

print_header "MethodCache Benchmark Runner"

echo -e "Category: ${GREEN}${BENCHMARK_CATEGORY}${NC}"
if [ "$QUICK_MODE" = true ]; then
    echo -e "Mode:     ${YELLOW}Quick (faster, less accurate)${NC}"
    export BENCHMARK_QUICK=true
else
    echo -e "Mode:     ${BLUE}Standard (more accurate, slower)${NC}"
fi
echo ""

# Clean if requested
if [ "$CLEAN" = true ]; then
    print_section "Cleaning previous builds..."
    dotnet clean "$BENCHMARK_PROJECT/$BENCHMARK_PROJECT.csproj" --configuration Release
    print_success "Clean completed"
fi

# Build the benchmark project
if [ "$SKIP_BUILD" = false ]; then
    print_section "Building benchmark project in Release mode..."

    dotnet build "$BENCHMARK_PROJECT/$BENCHMARK_PROJECT.csproj" \
        --configuration Release \
        --verbosity quiet \
        -p:TreatWarningsAsErrors=false

    if [ $? -eq 0 ]; then
        print_success "Build completed successfully"
    else
        print_error "Build failed"
        exit 1
    fi
else
    print_warning "Skipping build step"
fi

# Run benchmarks
print_section "Running benchmarks: $BENCHMARK_CATEGORY"
echo -e "${YELLOW}This may take several minutes depending on the category...${NC}"
echo ""

cd "$BENCHMARK_PROJECT"

dotnet run --configuration Release --no-build -- "$BENCHMARK_CATEGORY"

BENCHMARK_EXIT_CODE=$?

cd ..

echo ""

if [ $BENCHMARK_EXIT_CODE -eq 0 ]; then
    print_success "Benchmarks completed successfully!"
    echo ""

    # Show results
    print_header "Results Summary"

    RESULTS_PATH="${BENCHMARK_PROJECT}/${RESULTS_DIR}"

    if [ -d "$RESULTS_PATH" ]; then
        # Find the most recent markdown file
        LATEST_MD=$(find "$RESULTS_PATH" -name "*.md" -type f -print0 | xargs -0 ls -t | head -n 1)

        if [ -n "$LATEST_MD" ]; then
            echo ""
            cat "$LATEST_MD"
            echo ""
            print_section "Full results available at:"
            echo "  $LATEST_MD"

            # Also show JSON location
            LATEST_JSON=$(find "$RESULTS_PATH" -name "*-report-full.json" -type f -print0 | xargs -0 ls -t | head -n 1)
            if [ -n "$LATEST_JSON" ]; then
                echo "  $LATEST_JSON"
            fi
        else
            print_warning "No result files found in $RESULTS_PATH"
        fi
    else
        print_warning "Results directory not found: $RESULTS_PATH"
    fi

    echo ""
    print_success "Done! 🎉"
else
    print_error "Benchmarks failed with exit code: $BENCHMARK_EXIT_CODE"
    exit $BENCHMARK_EXIT_CODE
fi
