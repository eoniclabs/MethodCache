# Benchmarks

The numbers in the README come from [`ReadmeComparisonBenchmarks`](../MethodCache.Benchmarks/Comparison/ReadmeComparisonBenchmarks.cs):
a cache hit on `Task<User> GetUserAsync(int id)`, written the way each library documents it, with every library on its
default settings (MethodCache with statistics on) and workstation GC.

```bash
dotnet run -c Release --project MethodCache.Benchmarks -- readme --filter '*Hit'
```

## Cache hit (2026-10-11)

| Approach | Mean | Allocated | vs. IMemoryCache by hand |
|---|---:|---:|---:|
| `IMemoryCache` by hand (`TryGetValue` + `Set`) | 91 ns | 112 B | 1.00x |
| **MethodCache `[Cache]`** | **98 ns** | **160 B** | **1.08x** |
| HybridCache `GetOrCreateAsync` | 502 ns | 272 B | 5.5x |
| FusionCache `GetOrSetAsync` | 347 ns | 344 B | 3.8x |

```text
BenchmarkDotNet v0.15.4, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Xeon Processor 2.10GHz, 1 CPU, 4 logical and 4 physical cores
.NET 10.0.12, X64 RyuJIT x86-64-v4
IterationCount=15  WarmupCount=6  Server GC=False
Microsoft.Extensions.Caching.Hybrid 9.9.0, ZiggyCreatures.FusionCache 2.0.0
```

This is a shared cloud VM, so treat the numbers as about ±10 %, and run the benchmark on your own hardware before
relying on them. A MethodCache hit allocates the cache key (built from the method name and the arguments) and
returns the completed `Task` it stored, so it never allocates a new `Task`.

The class also has miss benchmarks (a new key on every call). They grow every cache without bound during the run, so
they measure the GC more than the libraries and are not quoted here.
