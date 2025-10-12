# Running Benchmarks

This project includes comprehensive performance benchmarks using BenchmarkDotNet.

## Quick Start

```bash
# Run quick benchmarks (fastest, good for development)
./run-benchmarks.sh quick

# Run specific benchmark category
./run-benchmarks.sh providers

# Run in quick mode for faster results
./run-benchmarks.sh providers --quick

# Run all benchmarks
./run-benchmarks.sh all

# Show the latest results without running
./run-benchmarks.sh --show-latest
```

## Benchmark Categories

- **basic** - Basic caching operations (hit/miss, different data sizes)
- **providers** - Compare different cache providers (InMemory, Redis, Hybrid)
- **concurrent** - Concurrent access and scalability tests
- **memory** - Memory usage and GC pressure analysis
- **realworld** - Real-world application scenarios
- **generic** - Generic interface performance
- **serialization** - Serialization performance comparison
- **quick** - Quick benchmarks for development (minimal parameters)
- **all** - Run all benchmark categories

## Options

- `--quick` - Run with fewer iterations for faster results (less accurate)
- `--clean` - Clean before building
- `--skip-build` - Skip the build step (use existing build)
- `--show-latest` - Display the most recent benchmark results

## Examples

```bash
# Quick development iteration
./run-benchmarks.sh quick

# Full provider comparison with clean build
./run-benchmarks.sh providers --clean

# Run all benchmarks in quick mode
./run-benchmarks.sh all --quick

# View latest results
./run-benchmarks.sh --show-latest
```

## Results Location

Results are saved to:
```
MethodCache.Benchmarks/BenchmarkDotNet.Artifacts/results/
```

Each benchmark run produces:
- Markdown summary (*.md)
- Full JSON report (*-report-full.json)
- HTML reports (*.html)

## Running from Benchmark Directory

You can also run benchmarks directly from the benchmark project:

```bash
cd MethodCache.Benchmarks
dotnet run -c Release -- basic
```

## Quick Mode vs Standard Mode

**Quick Mode** (`--quick` flag):
- Fewer warmup iterations
- Fewer actual runs
- Faster execution (good for development)
- Less accurate results
- Higher variance tolerance

**Standard Mode** (default):
- More iterations for statistical accuracy
- Longer execution time
- Production-ready results
- Lower variance tolerance

## Tips

1. **Development**: Use `quick` category or `--quick` flag for rapid iteration
2. **CI/CD**: Use specific categories without `--quick` for accurate results
3. **Comparison**: Always use the same mode when comparing results
4. **Environment**: Close unnecessary applications for more consistent results
5. **Multiple Runs**: Run benchmarks multiple times and compare for reliability
