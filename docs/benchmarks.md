# Benchmarks

The numbers in the README come from [`ReadmeComparisonBenchmarks`](../benchmarks/MethodCache.Benchmarks/Comparison/ReadmeComparisonBenchmarks.cs):
a cache hit on `Task<User> GetUserAsync(Guid id)`, written the way each library documents it, with every library on its
default settings (MethodCache with statistics on) and workstation GC. The id is a `Guid` because that is what most
applications key their data by.

```bash
dotnet run -c Release --project benchmarks/MethodCache.Benchmarks -- readme --filter '*Hit'
```

## Cache hit (2026-10-15)

| Approach | Mean | Allocated | vs. IMemoryCache by hand |
|---|---:|---:|---:|
| `IMemoryCache` by hand (`TryGetValue` + `Set`) | 115 ns | 176 B | 1.00x |
| **MethodCache `[Cache]`** | **100 ns** | **0 B** | **0.86x** |
| HybridCache `GetOrCreateAsync` | 574 ns | 352 B | 5.0x |
| FusionCache `GetOrSetAsync` | 385 ns | 416 B | 3.3x |

Each mean is the median of three runs. With an `int` id on the same machine, `IMemoryCache` by hand takes 93 ns and
allocates 112 B, and MethodCache takes 73 ns and allocates nothing: a GUID is a longer key for every library to build,
hash and compare.

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
forms). Record or DTO arguments use a string key, allocated on every hit. The table above was measured on the build
that includes the design series.

The class also has miss benchmarks (a new key on every call). They grow every cache without bound during the run, so
they measure the GC more than the libraries and are not quoted here.

## Other benchmarks

The project has more benchmarks: the in-memory engine on its own, the eviction policy's hit ratio, several libraries
behind one adapter interface, concurrency, memory use and more.
[benchmarks/MethodCache.Benchmarks/README.md](../benchmarks/MethodCache.Benchmarks/README.md) says which one answers
which question and how to run it.
