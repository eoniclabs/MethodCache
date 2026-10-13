# MethodCache Performance Benchmarks

This project contains comprehensive performance benchmarks for the MethodCache library, designed to measure and compare the performance characteristics of different caching scenarios, providers, and configurations.

## Overview

The benchmark suite includes:

- **Basic Caching Operations** - Fundamental cache hit/miss performance
- **Provider Scenarios** - InMemory hits, misses, and tag invalidation
- **Concurrent Access Patterns** - Thread safety and scalability testing
- **Memory Usage Analysis** - GC pressure and memory efficiency
- **Real-World Scenarios** - Application-specific use cases
- **Generic Interface Performance** - Performance of generic caching implementations
- **Serialization Comparisons** - Different key generation strategies

## Getting Started

### Prerequisites

- The .NET 10 SDK (the project targets `net10.0`)

### Running Benchmarks

```bash
# Build the project
dotnet build -c Release

# Run all benchmarks
dotnet run -c Release -- all

# Run specific benchmark categories
dotnet run -c Release -- basic
dotnet run -c Release -- providers
dotnet run -c Release -- concurrent
dotnet run -c Release -- memory
dotnet run -c Release -- realworld
dotnet run -c Release -- generic
dotnet run -c Release -- serialization
```

### Benchmark Categories

#### 1. Basic Caching Benchmarks (`basic`)

Tests fundamental caching operations with different data sizes:

- **Cache Miss** - First-time data access (cold cache)
- **Cache Hit** - Subsequent access to cached data
- **Cache Invalidation** - Performance of cache clearing operations
- **Multiple Cache Hits** - Batch operations performance

**Parameters:**
- `DataSize`: 1, 10, 100, 1000 items
- `ModelType`: Small, Medium, Large objects

#### 2. Cache Provider Comparison (`providers`)

Measures in-memory hits, misses and bulk tag invalidation. Hit data is populated in setup; misses clear
actual tagged entries. Redis and hybrid scenarios are not implemented and are excluded from measurements.


#### 3. Concurrent Access Benchmarks (`concurrent`)

Tests thread safety and scalability under concurrent load:

- **Concurrent Cache Hits** - Multiple threads accessing cached data
- **Concurrent Cache Misses** - Cache stampede scenarios
- **Mixed Operations** - Reads and invalidations
- **Same Key Access** - High contention scenarios
- **Bulk Invalidation** - Concurrent read/write operations

**Parameters:**
- `ThreadCount`: 2, 4, 8, 16 threads
- `OperationsPerThread`: 100, 1000 operations

#### 4. Memory Usage Benchmarks (`memory`)

Analyzes memory efficiency and GC pressure:

- **Cache Fill-up** - Memory impact of loading cache
- **Cache Eviction** - Memory behavior during cache turnover
- **Frequent Invalidation** - GC pressure from cache churn
- **Large Object Caching** - Impact of caching large objects
- **Short-lived Cache** - Memory efficiency of frequent expiration

**Parameters:**
- `CacheSize`: 100, 1000, 10000 items
- `DataType`: Small, Medium, Large objects

#### 5. Real-World Scenarios (`realworld`)

Simulates actual application usage patterns:

- **Web Application Dashboard** - User profile loading
- **E-Commerce Catalog** - Product browsing patterns
- **API Gateway** - External service call caching
- **Mobile App** - Offline-first caching strategy
- **Real-time Application** - Frequent cache updates
- **Analytics Reports** - Expensive computation caching

**Parameters:**
- `UserCount`: 50, 100, 200 users
- `ProductCount`: 100, 500, 1000 products

#### 6. Generic Interface Benchmarks (`generic`)

Tests performance of generic interface implementations:

- **Generic Repository** - Performance with different entity types
- **Mixed Operations** - Interleaved generic operations
- **Cache Invalidation** - Independent invalidation per type
- **Generic Methods** - Performance with generic constraints
- **Concurrent Generic Access** - Thread safety with generics

**Parameters:**
- `ItemCount`: 50, 100, 200 items

#### 7. Serialization Benchmarks (`serialization`)

Compares different cache key generation strategies:

- **MessagePack Serialization** - `MessagePackKeyGenerator` from MethodCache.Serialization.MessagePack
- **JSON Serialization** - Text-based serialization
- **ToString Serialization** - Simple string conversion

**Tests:**
- Simple object serialization performance
- Complex object serialization performance
- Cache key collision detection

**Parameters:**
- `ObjectCount`: 10, 100, 1000 objects
- `ObjectType`: Small, Medium, Large objects

## Configuration

### Benchmark Configuration

Most categories use one BenchmarkDotNet job, defined in `Program.cs`:

- **Runtime**: .NET 10 (`net10.0`)
- **Platform**: AnyCpu, so the machine's own architecture
- **GC**: Server GC with concurrent collection
- **Exporters**: JSON and GitHub Markdown

`readme` uses workstation GC instead, and `engine` uses BenchmarkDotNet's short run. Classes marked
`[MemoryDiagnoser]` also report allocations. Measured results are in [docs/benchmarks.md](../../docs/benchmarks.md).

## Interpreting Results

### Key Metrics

- **Mean**: Average execution time
- **Error**: Standard error of measurements
- **StdDev**: Standard deviation
- **Ratio**: Performance relative to baseline
- **Allocated**: Memory allocated per operation

### Performance Guidelines

**Cache Hit Performance:**
- Should be 10-100x faster than cache miss
- Memory allocation should be minimal
- Scales linearly with data size

**Cache Miss Performance:**
- Should be similar to non-cached operation + caching overhead
- Acceptable overhead: 10-50% for first access
- Should prevent cache stampedes

**Concurrent Performance:**
- Should scale with thread count up to CPU cores
- Lock contention should be minimal
- Cache stampede protection should be effective

**Memory Efficiency:**
- Memory usage should be proportional to cached data
- GC pressure should be minimal during steady state
- Cache eviction should be efficient

## Continuous Performance Monitoring

Run benchmark contract tests with `dotnet test tests/MethodCache.Benchmarks.Tests -c Release`.
For measurements, run the relevant category on a dedicated machine and retain the BenchmarkDotNet
report with runtime, hardware, package versions and workload parameters. Repository CI is defined
in `build/ci`; see `build/ci/README.md`.

## Contributing

When adding new benchmarks:

1. Follow the existing naming conventions
2. Use `[MemoryDiagnoser]` for memory analysis
3. Include baseline comparisons where appropriate
4. Document parameters and expected results
5. Test with different data sizes and scenarios

## Troubleshooting

**Memory Issues:**
- Increase available memory for large dataset benchmarks
- Use server GC configuration for better memory management
- Monitor system memory during benchmark execution

**Performance Variability:**
- Run benchmarks on dedicated machines when possible
- Close unnecessary applications during benchmarking
- Use consistent system configuration across runs

For more information about BenchmarkDotNet configuration and best practices, visit: https://benchmarkdotnet.org/

The memory benchmark no-caching baseline calls the uncached data source directly, so warmup and repeated invocations cannot turn it into a cache-hit benchmark.
