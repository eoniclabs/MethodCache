#!/bin/bash

# MethodCache Performance Benchmarks Runner
# Usage: ./run-benchmarks.sh [category] [options]

set -e

# Script configuration
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR"
CONFIGURATION="Release"
OUTPUT_FORMAT="console"
VERBOSE=false
REDIS_ENABLED=false
FILTER=""
WARMUP=3
ITERATIONS=5
QUICK=false

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
GRAY='\033[0;37m'
NC='\033[0m' # No Color

# Helper functions
print_header() {
    echo -e "${GREEN}$1${NC}"
    echo -e "${GREEN}$(echo "$1" | sed 's/./=/g')${NC}"
    echo
}

print_success() {
    echo -e "${GREEN}✓ $1${NC}"
}

print_warning() {
    echo -e "${YELLOW}⚠ $1${NC}"
}

print_error() {
    echo -e "${RED}❌ $1${NC}"
}

print_info() {
    echo -e "${CYAN}📊 $1${NC}"
}

show_usage() {
    cat << EOF
MethodCache Performance Benchmarks Runner

Usage: $0 <category> [options]

Categories:
  basic         Basic caching operations (hit/miss, different data sizes)
  providers     Compare different cache providers (InMemory, Redis, Hybrid)
  concurrent    Concurrent access and scalability tests
  memory        Memory usage and GC pressure analysis
  realworld     Real-world application scenarios
  generic       Generic interface performance
  serialization Serialization performance comparison
  all           Run all benchmark categories

Options:
  -f, --format FORMAT     Output format (console, html, csv, json) [default: console]
  -c, --config CONFIG     Build configuration (Debug, Release) [default: Release]
  --filter PATTERN        Filter benchmarks by method name pattern
  --warmup COUNT          Number of warmup iterations [default: 3]
  --iterations COUNT      Number of measurement iterations [default: 5]
  --quick                 Use lightweight benchmark job (same as -- -q)
  --redis                 Enable Redis provider benchmarks (requires Redis server)
  --verbose               Enable verbose output
  -h, --help              Show this help message

Examples:
  $0 basic --format html
  $0 providers --redis --verbose
  $0 concurrent --filter "*Concurrent*"
  $0 all --config Release --redis

Prerequisites:
  - .NET 9.0 or later
  - Redis server (optional, for Redis provider benchmarks)

EOF
}

check_prerequisites() {
    echo -e "${YELLOW}Checking prerequisites...${NC}"
    
    # Check .NET
    if command -v dotnet &> /dev/null; then
        DOTNET_VERSION=$(dotnet --version)
        print_success ".NET version: $DOTNET_VERSION"
    else
        print_error ".NET is not installed or not in PATH"
        exit 1
    fi
    
    # Check Redis if required
    if [[ "$REDIS_ENABLED" == true ]] || [[ "$CATEGORY" == "providers" ]] || [[ "$CATEGORY" == "all" ]]; then
        echo -e "${YELLOW}Checking Redis connectivity...${NC}"
        
        if command -v redis-cli &> /dev/null; then
            if redis-cli ping &> /dev/null; then
                print_success "Redis connection successful"
            else
                print_warning "Redis server not responding - Redis benchmarks will be skipped"
                show_redis_setup_help
            fi
        else
            print_warning "Redis CLI not found - Redis benchmarks may fail"
            show_redis_setup_help
        fi
    fi
}

show_redis_setup_help() {
    echo -e "${GRAY}  To enable Redis benchmarks:${NC}"
    echo -e "${GRAY}    Docker: docker run -d -p 6379:6379 redis:alpine${NC}"
    echo -e "${GRAY}    macOS: brew install redis && redis-server${NC}"
    echo -e "${GRAY}    Linux: sudo apt-get install redis-server && redis-server${NC}"
}

build_project() {
    echo
    echo -e "${YELLOW}Building project...${NC}"
    
    if dotnet build -c "$CONFIGURATION" --nologo > /dev/null 2>&1; then
        print_success "Build successful"
    else
        print_error "Build failed"
        exit 1
    fi
}

run_benchmarks() {
    local category="$1"
    
    # Prepare benchmark arguments
    local benchmark_args=("$category")
    
    if [[ -n "$FILTER" ]]; then
        benchmark_args+=("--filter" "$FILTER")
    fi
    
    if [[ "$QUICK" == true ]]; then
        benchmark_args+=("--quick")
    fi
    
    # Set environment variables
    export BENCHMARK_OUTPUT_FORMAT="$OUTPUT_FORMAT"
    export BENCHMARK_WARMUP_COUNT="$WARMUP"
    export BENCHMARK_ITERATION_COUNT="$ITERATIONS"
    export BENCHMARK_VERBOSE="$VERBOSE"
    if [[ "$QUICK" == true ]]; then
        export BENCHMARK_QUICK="true"
    fi
    
    # Create output directory
    local output_dir="$PROJECT_DIR/BenchmarkResults"
    mkdir -p "$output_dir"
    
    # Run benchmarks
    echo
    echo -e "${YELLOW}Running benchmarks...${NC}"
    echo -e "${GRAY}Category: $category${NC}"
    echo -e "${GRAY}Configuration: $CONFIGURATION${NC}"
    echo -e "${GRAY}Output Format: $OUTPUT_FORMAT${NC}"
    [[ -n "$FILTER" ]] && echo -e "${GRAY}Filter: $FILTER${NC}"
    echo
    
    local timestamp=$(date +"%Y%m%d-%H%M%S")
    local log_file="$output_dir/benchmark-$category-$timestamp.log"
    
    if [[ "$VERBOSE" == true ]]; then
        dotnet run -c "$CONFIGURATION" --no-build -- "${benchmark_args[@]}" 2>&1 | tee "$log_file"
        status=$?
    else
        dotnet run -c "$CONFIGURATION" --no-build -- "${benchmark_args[@]}" > "$log_file" 2>&1
        status=$?
    fi

    if [[ $status -ne 0 ]]; then
        print_error "Benchmark execution failed with exit code $status"
        exit $status
    fi
    
    print_success "Benchmarks completed successfully"
    
    # Copy artifacts if they exist
    local artifacts_dir="$PROJECT_DIR/BenchmarkDotNet.Artifacts"
    if [[ -d "$artifacts_dir" ]]; then
        print_info "Benchmark artifacts available in: $artifacts_dir"
        
        # Copy results to output directory
        cp -r "$artifacts_dir"/* "$output_dir/" 2>/dev/null || true
        print_info "Results copied to: $output_dir"
    fi
    
    return 0
}

generate_summary() {
    local category="$1"
    local output_dir="$PROJECT_DIR/BenchmarkResults"
    
    echo
    print_header "Benchmark Summary"
    echo "Category: $category"
    echo "Configuration: $CONFIGURATION"
    echo "Completed: $(date)"
    echo "Results: $output_dir"
    echo
    
    if [[ "$OUTPUT_FORMAT" == "html" ]]; then
        local html_files=$(find "$output_dir" -name "*.html" 2>/dev/null || true)
        if [[ -n "$html_files" ]]; then
            print_info "HTML Reports generated:"
            echo "$html_files" | while read -r file; do
                echo -e "${GRAY}  $file${NC}"
            done
            echo
        fi
    fi
    
    print_success "Benchmark run completed!"
}

# Parse command line arguments
if [[ $# -eq 0 ]]; then
    show_usage
    exit 1
fi

CATEGORY="$1"
shift

# Validate category
case "$CATEGORY" in
    basic|providers|concurrent|memory|realworld|generic|serialization|all)
        ;;
    -h|--help)
        show_usage
        exit 0
        ;;
    *)
        print_error "Invalid category: $CATEGORY"
        show_usage
        exit 1
        ;;
esac

# Parse options
while [[ $# -gt 0 ]]; do
    case $1 in
        -f|--format)
            OUTPUT_FORMAT="$2"
            case "$OUTPUT_FORMAT" in
                console|html|csv|json)
                    ;;
                *)
                    print_error "Invalid output format: $OUTPUT_FORMAT"
                    exit 1
                    ;;
            esac
            shift 2
            ;;
        -c|--config)
            CONFIGURATION="$2"
            case "$CONFIGURATION" in
                Debug|Release)
                    ;;
                *)
                    print_error "Invalid configuration: $CONFIGURATION"
                    exit 1
                    ;;
            esac
            shift 2
            ;;
        --filter)
            FILTER="$2"
            shift 2
            ;;
        --warmup)
            WARMUP="$2"
            shift 2
            ;;
        --iterations)
            ITERATIONS="$2"
            shift 2
            ;;
        --redis)
            REDIS_ENABLED=true
            shift
            ;;
        --verbose)
            VERBOSE=true
            shift
            ;;
        --quick)
            QUICK=true
            shift
            ;;
        -h|--help)
            show_usage
            exit 0
            ;;
        *)
            print_error "Unknown option: $1"
            show_usage
            exit 1
            ;;
    esac
done

# Main execution
print_header "MethodCache Performance Benchmarks"

check_prerequisites
build_project
run_benchmarks "$CATEGORY"
generate_summary "$CATEGORY"
