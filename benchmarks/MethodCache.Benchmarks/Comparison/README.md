# MethodCache Performance Comparison Strategy

## Overview

This framework provides **two distinct comparison approaches** to address the fundamental challenge of comparing source-generated code (MethodCache) with runtime-based caching libraries:

1. **Real Usage Comparison** (`ReadmeComparisonBenchmarks.cs`, `readme`) - Each library used the way its documentation shows; these are the numbers in the README, with the run details in [docs/benchmarks.md](../../../docs/benchmarks.md)
2. **Adapter-Based Comparison** (`UnifiedCacheComparisonBenchmarks.cs`, `comparison`) - Normalized comparison through common interface

**Important**: These serve different purposes and will show different results. Understanding the difference is critical for proper interpretation.

## Supported Libraries

The package versions are in `MethodCache.Benchmarks.csproj`.

| Library | Benchmarks | Stampede protection |
|---------|------------|---------------------|
| **MethodCache** | `readme`, `comparison` | Yes |
| **FusionCache** | `readme`, `comparison` | Yes |
| **HybridCache** (`Microsoft.Extensions.Caching.Hybrid`) | `readme` | Yes |
| **LazyCache** | `comparison` | Yes |
| **EasyCaching** | `comparison` | Yes |
| **Microsoft.Extensions.Caching.Memory** | `readme` (by hand), adapter contract tests | No |

BitFaster.Caching appears only in the `engine` and `hitratio` benchmarks, not in either comparison.

## The Adapter Problem

### Why Two Comparison Approaches?

MethodCache uses **compile-time source generation** to create optimized caching code, while other frameworks use **runtime abstractions** (proxies, interceptors, decorators). This fundamental difference creates a measurement challenge:

**Through Adapter Interface** (`ICacheAdapter`): every library, MethodCache included, goes through the same generic
`string key` interface. The runtime-key adapter builds policies and generates keys per operation; the manual-key
adapter uses storage keys directly. The SourceGen adapter uses generated get-or-create methods, while ordinary
lookup reads warmed storage directly.

**Real Usage** (source-generated code): MethodCache runs the decorator the source generator writes for the method.
This is what an application gets; [docs/benchmarks.md](../../../docs/benchmarks.md) has the measured numbers.

### The Root Cause: Key and Policy Handling at Run Time

The runtime-key adapter builds a runtime policy and passes the key through `ICacheManager` on every
operation. A generated decorator has the method's policy and key layout fixed at compile time, writes the key into a
stack buffer and looks it up without making a string.

The difference between the two is adapter overhead that real MethodCache usage avoids. Run both comparisons on your
own hardware to see its size; this page does not quote numbers that are not reproduced by a checked-in run.

## Architecture

### ICacheAdapter Interface

All libraries implement a common interface:

```csharp
public interface ICacheAdapter : IDisposable
{
    string Name { get; }
    Task<TValue> GetOrSetAsync<TValue>(string key, Func<Task<TValue>> factory, TimeSpan duration);
    bool TryGet<TValue>(string key, out TValue? value);
    void Set<TValue>(string key, TValue value, TimeSpan duration);
    void Remove(string key);
    void Clear();
    CacheStatistics GetStatistics();
}
```

### Adapters

Each library has a dedicated adapter that translates between the common interface and the library's native API:

- `MethodCacheSourceGenAdapter` - A source-generated decorator (`MethodCache_SourceGen_*`)
- `DirectApiMethodCacheAdapter` - `ICacheManager` with keys the caller supplies (`MethodCache_ManualKey_*`)
- `RuntimeKeyMethodCacheAdapter` - `ICacheManager` with keys generated at run time on every call (`MethodCache_RuntimeKey_*`)
- `OptimizedMethodCacheAdapter` - `ICacheManager` with a cached policy and fewer allocations
- `StaticKeyMethodCacheAdapter` - a key prefix built once in setup, with the caller's key appended
- `FusionCacheAdapter` - Wraps FusionCache
- `LazyCacheAdapter` - Wraps LazyCache/IAppCache
- `EasyCachingAdapter` - Wraps IEasyCachingProvider
- `MemoryCacheAdapter` - Wraps IMemoryCache

`UnifiedCacheComparisonBenchmarks` uses the first three and the FusionCache, LazyCache and EasyCaching adapters. The
others are covered by the adapter contract tests in `tests/MethodCache.Benchmarks.Tests`, which check that set,
lookup, get-or-create and remove share one key identity, that clear removes entries, that zero is a valid cached
value and that each adapter owns an isolated cache.

## Test Scenarios

Each category is grouped separately in the results (`CacheHit`, `CacheHitAsync`, `MissAndSet`, `Concurrent`,
`ConcurrentHits`, `Stampede`). There is no baseline row; compare the libraries within a category.

### 1. Cache Hit Performance
**Tests:** Reads of a key warmed before each iteration (`CacheHit` through `TryGet`; `CacheHitAsync`, MethodCache only, calls the generated decorator directly)
**Purpose:** Measure the overhead of cache retrieval

### 2. Cache Miss + Set
**Tests:** A new key on every call, so the factory runs and the value is stored
**Purpose:** Measure the cost of cache misses and storing values

### 3. Concurrent Access
**Tests:** `Concurrent`: 10 and 100 tasks at once; `ConcurrentHits`: each task reads a small set of hot keys 256 times
**Purpose:** Test thread-safety and contention handling

### 4. Cache Stampede Protection
**Tests:** 50 concurrent requests for the same missing key; the factory only yields, so the time measured is the
coordination between the requests
**Purpose:** Measure the cost of stampede protection

## Running Comparisons

### Real Usage Comparison (Recommended)
Shows actual framework performance - **use these results for decision-making**:

```bash
dotnet run -c Release -- readme --filter '*Hit'
```

**Results**: see [docs/benchmarks.md](../../../docs/benchmarks.md) for the latest measured run and its environment.

### Adapter-Based Comparison
Normalized comparison through common interface - useful for understanding relative capabilities:

```bash
# Full comparison
dotnet run -c Release -- comparison

# Quick test (less accurate, faster)
BENCHMARK_QUICK=true dotnet run -c Release -- comparison

# Filter by category
dotnet run -c Release -- comparison --filter *CacheHit*
dotnet run -c Release -- comparison --filter *Stampede*
dotnet run -c Release -- comparison --filter *Concurrent*
```

**Note**: These results include the adapter overhead described above for MethodCache.

## Our Solution: Dual Comparison Strategy

We acknowledge both comparisons are valuable but serve different purposes:

### ✅ Use Real Usage Results When:
- **Choosing a caching framework** - You want actual performance numbers
- **Estimating performance impact** - You need realistic measurements
- **Reporting benchmarks publicly** - Show how the frameworks actually perform
- **Making architectural decisions** - Performance is a key factor

### ⚠️ Use Adapter-Based Results When:
- **Comparing normalized implementations** - Same workload through same interface
- **Testing specific scenarios** - Stampede prevention, concurrent access patterns
- **Understanding relative capabilities** - How frameworks handle edge cases
- **Academic interest** - Analyzing design tradeoffs

### ❌ Never:
- Treat different cache access paths as equivalent application workloads
- Compare real-usage MethodCache numbers to adapter-based other framework numbers
- Mix results from both comparison types in the same analysis

## Understanding Results

### Key Metrics

- **Mean**: Average execution time
- **Error**: Half of 99.9% confidence interval
- **StdDev**: Standard deviation (consistency indicator)
- **Ratio**: Performance relative to baseline
- **Rank**: Relative ranking (1 = fastest)
- **Allocated**: Memory allocated per operation

### What to Look For

1. **Cache Hits**: Lower is better - measures cache overhead
2. **Miss + Set**: Includes factory execution - should be similar across libraries
3. **Concurrent**: Tests scalability - look for linear vs exponential scaling
4. **Stampede**: Lower is better; the benchmark does not count factory calls

## Adding New Libraries

To add a new caching library:

1. **Add NuGet Package** to `MethodCache.Benchmarks.csproj`

2. **Create Adapter** in `Comparison/Adapters/YourLibraryAdapter.cs`:
```csharp
public class YourLibraryAdapter : ICacheAdapter
{
    public string Name => "YourLibrary";

    public async Task<TValue> GetOrSetAsync<TValue>(...)
    {
        // Implement using library's API
    }

    // Implement other interface methods...
}
```

3. **Add to Benchmarks** in `UnifiedCacheComparisonBenchmarks.cs`:
```csharp
private ICacheAdapter _yourLibrary = null!;

[GlobalSetup]
public void GlobalSetup()
{
    _yourLibrary = new YourLibraryAdapter();
    // ...
}

[BenchmarkCategory("CacheHit"), Benchmark]
public bool YourLibrary_Hit()
{
    return _yourLibrary.TryGet<SamplePayload>(TestKey, out _);
}
```

4. **Build and Run**:
```bash
dotnet build benchmarks/MethodCache.Benchmarks -c Release
dotnet run --project benchmarks/MethodCache.Benchmarks -c Release -- comparison
```

## Interpretation Guide

### MethodCache Expected Advantages

1. **Compile-Time Code Generation**
   - Zero reflection overhead
   - Optimized method calls
   - Better JIT optimization opportunities

2. **Lightweight Runtime**
   - Minimal abstraction layers
   - Direct cache access
   - No proxy creation overhead

### Where Runtime Libraries May Excel

1. **Dynamic Scenarios**
   - No source code access needed
   - Works with interfaces/abstract classes
   - Runtime configuration flexibility

2. **Advanced Features**
   - Some libraries have richer feature sets
   - May include distributed caching out-of-box
   - Additional resilience patterns

## Example Results

The measured real-usage results, with the hardware and versions they were taken on, are in
[docs/benchmarks.md](../../../docs/benchmarks.md). Adapter-based results are not published: run `comparison`
yourself, and remember that its MethodCache rows include the adapter overhead.

## Contributing

When adding new comparison scenarios:

1. **Keep it Fair**: All libraries should do the same work
2. **Measure What Matters**: Focus on real-world patterns
3. **Document Assumptions**: Explain what each test validates
4. **Consider Context**: Some libraries optimize for different scenarios

## References

- **MethodCache Core**: [`src/MethodCache.Core`](../../../src/MethodCache.Core)
- **Benchmark Best Practices**: https://benchmarkdotnet.org/articles/guides/good-practices.html

After removal of raw-key attributes, generated service calls use method-scoped keys. `GetAsyncDirect` measures generated lookup; adapter operations that accept arbitrary factories use the public manual API and a separately populated manual key. Do not interpret those factory timings as a generated decorator measurement.
