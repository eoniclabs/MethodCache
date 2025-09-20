# MethodCache Performance Benchmarks

This project contains comprehensive performance benchmarks for the MethodCache library, designed to measure and compare the performance characteristics of different caching scenarios, providers, and configurations.

## Overview

The benchmark suite includes:

- **Basic Caching Operations** - Fundamental cache hit/miss performance
- **Cache Provider Comparisons** - InMemory, Hybrid, and Redis provider performance
- **Concurrent Access Patterns** - Thread safety and scalability testing
- **Memory Usage Analysis** - GC pressure and memory efficiency
- **Real-World Scenarios** - Application-specific use cases
- **Generic Interface Performance** - Performance of generic caching implementations
- **Serialization Comparisons** - Different key generation strategies

## Getting Started

### Prerequisites

- .NET 9.0 or later
- Optional: Redis server (for Redis provider benchmarks)

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

**Example Results:**
```
| Method              | DataSize | ModelType | Mean      | Error     | StdDev    | Ratio | Allocated |
|-------------------- |--------- |---------- |----------:|----------:|----------:|------:|----------:|
| NoCaching           | 100      | Small     | 1.234 ms  | 0.021 ms  | 0.018 ms  | 1.00  |   1.2 KB  |
| CacheMiss           | 100      | Small     | 1.456 ms  | 0.028 ms  | 0.025 ms  | 1.18  |   2.1 KB  |
| CacheHit            | 100      | Small     | 0.089 ms  | 0.002 ms  | 0.002 ms  | 0.07  |   0.8 KB  |
```

#### 2. Cache Provider Comparison (`providers`)

Compares performance across different cache providers:

- **InMemory Cache** - Default in-memory implementation
- **Hybrid Cache** - L1 (memory) + L2 (Redis) hybrid approach
- **Redis Cache** - Pure Redis implementation (if available)

**Parameters:**
- `ItemCount`: 10, 100, 1000 items
- `DataType`: Small, Medium objects

**Tests:**
- Cache hits performance
- Cache misses performance
- Bulk invalidation performance

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

- **MessagePack Serialization** - Binary serialization (default)
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

### Redis Setup (Optional)

For Redis provider benchmarks, ensure Redis is running:

```bash
# Using Docker
docker run -d -p 6379:6379 redis:alpine

# Or install Redis locally
# Windows: Use Redis for Windows
# macOS: brew install redis && redis-server
# Linux: sudo apt-get install redis-server && redis-server
```

### Benchmark Configuration

The benchmarks use BenchmarkDotNet with the following configuration:

- **Runtime**: .NET 9.0
- **Platform**: x64
- **GC**: Server GC with concurrent collection
- **Toolchain**: In-process emission for faster execution
- **Memory Diagnostics**: Enabled for memory analysis

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

## Example Output

```
BenchmarkDotNet v0.14.0, Windows 11 (10.0.22631.4317/23H2/2023Update/SunValley3)
12th Gen Intel Core i7-12700K, 1 CPU, 20 logical and 12 physical cores
.NET 9.0.0 (9.0.24.52809), X64 RyuJIT AVX2

| Method          | ItemCount | Provider | Mean      | Error     | StdDev    | Ratio | Gen0   | Allocated |
|---------------- |---------- |--------- |----------:|----------:|----------:|------:|-------:|----------:|
| InMemory_Hits   | 100       | InMemory | 89.23 μs  | 1.234 μs  | 1.155 μs  | 1.00  | 0.4883 |   2.01 KB |
| Hybrid_Hits     | 100       | Hybrid   | 92.45 μs  | 1.789 μs  | 1.674 μs  | 1.04  | 0.4883 |   2.13 KB |
| Redis_Hits      | 100       | Redis    | 145.67 μs | 2.456 μs  | 2.298 μs  | 1.63  | 0.2441 |   1.87 KB |
```

## Continuous Performance Monitoring

The benchmarks can be integrated into CI/CD pipelines for continuous performance monitoring:

```yaml
# Example GitHub Actions workflow
- name: Run Performance Benchmarks
  run: |
    cd MethodCache.Benchmarks
    dotnet run -c Release -- basic > benchmark-results.txt
    
- name: Upload Results
  uses: actions/upload-artifact@v3
  with:
    name: benchmark-results
    path: benchmark-results.txt
```

## Contributing

When adding new benchmarks:

1. Follow the existing naming conventions
2. Use `[MemoryDiagnoser]` for memory analysis
3. Include baseline comparisons where appropriate
4. Document parameters and expected results
5. Test with different data sizes and scenarios

## Troubleshooting

**Redis Connection Issues:**
- Ensure Redis server is running on localhost:6379
- Check firewall and network connectivity
- Redis benchmarks will be skipped if connection fails

**Memory Issues:**
- Increase available memory for large dataset benchmarks
- Use server GC configuration for better memory management
- Monitor system memory during benchmark execution

**Performance Variability:**
- Run benchmarks on dedicated machines when possible
- Close unnecessary applications during benchmarking
- Use consistent system configuration across runs

For more information about BenchmarkDotNet configuration and best practices, visit: https://benchmarkdotnet.org/