# MethodCache Benchmarking Guide

Which benchmark answers which question, and how to run it. Measured results, with the hardware and versions they
came from, are in [docs/benchmarks.md](../../docs/benchmarks.md); this guide quotes no numbers, because they depend on
the machine and are only meaningful next to the run that produced them.

## Which benchmark to use

| Question | Benchmark | Command |
|---|---|---|
| How fast is a `[Cache]` hit compared with other libraries, used the way each documents? | `ReadmeComparisonBenchmarks` | `readme` |
| How do libraries compare behind one interface (hits, misses, concurrency, stampedes)? | `UnifiedCacheComparisonBenchmarks` | `comparison` |
| How fast is the in-memory engine on its own? | `MemoryEngineBenchmarks` | `engine` |
| What hit ratio does the eviction policy reach on skewed workloads? | `HitRatioSimulation` (a simulation, not BenchmarkDotNet) | `hitratio` |

Use `readme` to choose a library or to compare against the README: it calls a source-generated decorator, which is
what an application runs. `comparison` routes every library, MethodCache included, through the same `ICacheAdapter`
with `string` keys, so MethodCache pays for runtime key and policy handling that generated code does at compile time.
It is useful for comparing behaviour under the same workload, not for absolute MethodCache numbers.
[Comparison/README.md](Comparison/README.md) explains the adapters.

## Running

From the repository root, always in Release:

```bash
dotnet run -c Release --project benchmarks/MethodCache.Benchmarks -- <category> [--quick] [BenchmarkDotNet options]
```

| Category | Runs |
|---|---|
| `readme` | `ReadmeComparisonBenchmarks`: MethodCache `[Cache]`, `IMemoryCache` by hand, HybridCache, FusionCache |
| `comparison` | `UnifiedCacheComparisonBenchmarks`: MethodCache, FusionCache, LazyCache, EasyCaching through `ICacheAdapter` |
| `engine` | `MemoryEngineBenchmarks` |
| `hitratio` | `HitRatioSimulation` |
| `basic` | `BasicCachingBenchmarks`: hits and misses with different payload sizes |
| `baseline` | `BaselineComparisonBenchmarks`: MethodCache against `IMemoryCache` and LazyCache |
| `providers` | `CacheProviderComparisonBenchmarks`: in-memory hits, misses and tag invalidation |
| `concurrent` | `ConcurrentAccessBenchmarks` |
| `memory` | `MemoryUsageBenchmarks` |
| `realworld` | `RealWorldScenarioBenchmarks` |
| `generic` | `GenericInterfaceBenchmarks` |
| `serialization` | `SerializationBenchmarks` |
| `quick` | `QuickCachingBenchmarks`, a small set for development |
| `profile` | `SourceGenSyncPathProfiler`: the generated decorator's sync and async hit paths |
| `all` | `basic`, `providers`, `concurrent`, `memory`, `realworld`, `generic` and `serialization` in turn |

`readme`, `comparison` and `engine` pass the remaining arguments to BenchmarkDotNet, so `--filter` works there:

```bash
# The README numbers (cache hits only)
dotnet run -c Release --project benchmarks/MethodCache.Benchmarks -- readme --filter '*Hit'

# One category of the adapter comparison
dotnet run -c Release --project benchmarks/MethodCache.Benchmarks -- comparison --filter '*Stampede*'
```

`UnifiedCacheComparisonBenchmarks` groups its methods by category: `CacheHit`, `CacheHitAsync`, `MissAndSet`,
`Concurrent`, `ConcurrentHits` and `Stampede`. MethodCache appears in three modes:

- `MethodCache_SourceGen_*`: a source-generated decorator behind the adapter.
- `MethodCache_ManualKey_*`: `ICacheManager` with keys the caller supplies.
- `MethodCache_Legacy_*`: keys generated at run time on every call; kept for comparison only.

`--quick` (or `BENCHMARK_QUICK=true`) uses fewer warmups and iterations. It is for checking that a benchmark runs, not
for numbers you publish; `engine` always uses BenchmarkDotNet's short run. Results land in
`BenchmarkDotNet.Artifacts/results/` under the directory you ran from.

## Publishing numbers

Quote a number only next to the run it came from: the BenchmarkDotNet environment summary (runtime, CPU, GC mode),
the package versions of the libraries compared, and the date. Update [docs/benchmarks.md](../../docs/benchmarks.md)
and the README together. A shared or cloud machine varies by several percent between runs, so rerun before drawing
conclusions from small differences.

`tests/MethodCache.Benchmarks.Tests` compiles the benchmark sources and checks that their setup works (adapters,
registrations, warmed caches) on .NET 8, 9 and 10. Run it after changing a benchmark:

```bash
dotnet test tests/MethodCache.Benchmarks.Tests -c Release
```

The memory benchmark no-caching baseline calls the uncached data source directly, so warmup and repeated invocations cannot turn it into a cache-hit benchmark.
