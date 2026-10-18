[← MethodCache documentation](README.md)

# Benchmarks

The numbers in the README come from [`ReadmeComparisonBenchmarks`](../benchmarks/MethodCache.Benchmarks/Comparison/ReadmeComparisonBenchmarks.cs):
a cache hit on `Task<User> GetUserAsync(int id)`, and on the same method with a `Guid` id, written the way each library
documents it, with every library on its default settings (including MethodCache's built-in counters) and workstation GC. Most
applications key their data by `Guid`, which is a longer key for every library to build, hash and compare.

```bash
dotnet run -c Release --project benchmarks/MethodCache.Benchmarks -- readme --filter '*Hit'
```

## Cache hit (2026-10-15)

| Approach | `int` id | Allocated | vs. IMemoryCache by hand | `Guid` id | Allocated | vs. IMemoryCache by hand |
|---|---:|---:|---:|---:|---:|---:|
| `IMemoryCache` by hand (`TryGetValue` + `Set`) | 93 ns | 112 B | 1.00x | 114 ns | 176 B | 1.00x |
| **MethodCache `[Cache]`** | **77 ns** | **0 B** | **0.82x** | **102 ns** | **0 B** | **0.90x** |
| HybridCache `GetOrCreateAsync` | 551 ns | 272 B | 5.9x | 597 ns | 352 B | 5.2x |
| FusionCache `GetOrSetAsync` | 354 ns | 344 B | 3.8x | 389 ns | 416 B | 3.4x |

Table values are medians across three benchmark runs of both id types. Single runs of the same benchmark differed by up to 14 %.
Design-series acceptance uses separate baseline/PR comparisons and 95% intervals over paired head/base ratios
from independent process launches;
see the [current design verification](developer/design-final-validation.md).

```text
BenchmarkDotNet v0.15.4, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Xeon Processor 2.10GHz, 1 CPU, 4 logical and 4 physical cores
.NET 10.0.12, X64 RyuJIT x86-64-v4
IterationCount=15  WarmupCount=6  Server GC=False
Microsoft.Extensions.Caching.Hybrid 9.9.0, ZiggyCreatures.FusionCache 2.0.0
```

This is a shared cloud VM, so treat the numbers as about ±10 %, and run the benchmark on your own hardware before
relying on them. A MethodCache hit writes the cache key (method name and arguments) into a stack buffer, looks it up
without making a string, and returns the completed `Task` it stored, so it allocates nothing. On the supported .NET 10
runtime this applies when every argument is a simple value (numbers, strings, enums, `Guid`, dates and their nullable
forms). Record or DTO arguments use a string key, allocated on every hit. The table records the 2026-10-15 build of the design series; the linked current
design verification tracks later revisions and their separate acceptance results.

The class also has miss benchmarks (a new key on every call). They grow every cache without bound during the run, so
they measure the GC more than the libraries and are not quoted here.

## Other benchmarks

The project has more benchmarks: the in-memory engine on its own, the eviction policy's hit ratio, several libraries
behind one adapter interface, concurrency, memory use and more.
[benchmarks/MethodCache.Benchmarks/README.md](../benchmarks/MethodCache.Benchmarks/README.md) says which one answers
which question and how to run it.
