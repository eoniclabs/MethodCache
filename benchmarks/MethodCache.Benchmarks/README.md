# MethodCache Benchmarks

Which benchmark answers which question, and how to run it. Measured results, with the hardware and versions they
came from, are in [docs/benchmarks.md](../../docs/benchmarks.md); this page quotes no numbers, because they depend on
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

You need the .NET 10 SDK (the project targets `net10.0`). From the repository root, always in Release:

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
| `serialization` | `SerializationBenchmarks`: cache key generators (MessagePack, JSON, `ToString`) |
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
for numbers you publish. Results land in `BenchmarkDotNet.Artifacts/results/` under the directory you ran from.

### How the runs are configured

Most categories use one BenchmarkDotNet job, defined in `Program.cs`: the machine's own architecture, Server GC with
concurrent collection, and JSON and GitHub Markdown exporters. In quick mode it runs 1 warmup and 3 measured
iterations. `readme` uses workstation GC, with 6 warmups and 15 iterations (3 and 5 in quick mode), and `engine`
always uses BenchmarkDotNet's short run. Classes marked `[MemoryDiagnoser]` also report allocations.

## Publishing numbers

Quote a number only next to the run it came from: the BenchmarkDotNet environment summary (runtime, CPU, GC mode),
the package versions of the libraries compared, and the date. Update [docs/benchmarks.md](../../docs/benchmarks.md)
and the README together. A shared or cloud machine varies by several percent between runs, so rerun before drawing
conclusions from small differences.

## Setup checks

`tests/MethodCache.Benchmarks.Tests` compiles the benchmark sources and checks that their setup works, on .NET 8, 9
and 10. Run it after changing a benchmark:

```bash
dotnet test tests/MethodCache.Benchmarks.Tests -c Release
```

It uses the actual benchmark sources, including the dependency-injection registrations, and checks that both base
classes can populate and read the cache before a measurement runs. Adapter contract tests cover all nine adapters:
one key identity across set, lookup, get-or-create and removal; overwrite; clear; value-type zero; and isolation
between instances. MethodCache adapter expiration is checked with `FakeTimeProvider`. Scenario checks ensure each
concurrent miss invocation runs its factories, invalidation removes the entries used by the scenario, and provider
hits reuse populated values.

The source-generator adapter uses an explicit raw key for its shared operations and receives each factory as a method
argument. Its direct async hit benchmark still calls the generated decorator; its `TryGet` operation uses a lookup
that cannot run a factory.

## The scenario benchmarks

### `basic`: BasicCachingBenchmarks

No caching (the baseline), a cache miss, a cache hit, a hit without warmup, invalidation and several hits in a row.
Parameters: `DataSize` 1, 10, 100, 1000 items; `ModelType` Small, Medium, Large.

### `providers`: CacheProviderComparisonBenchmarks

In-memory hits, misses and bulk tag invalidation. Hit data is populated in setup; misses clear the tagged entries
first. Parameters: `ItemCount` 10, 100, 1000; `DataType` Small, Medium.

### `concurrent`: ConcurrentAccessBenchmarks

Concurrent hits, concurrent misses, mixed reads and invalidations, many threads on the same key, bulk invalidation
during reads, and a cache stampede. Parameters: `ThreadCount` 2, 4, 8, 16; `OperationsPerThread` 100, 1000.

### `memory`: MemoryUsageBenchmarks

Filling the cache, hits after filling it, eviction, frequent invalidation, large objects, short-lived entries and
memory pressure, against a no-caching baseline. Parameters: `CacheSize` 100, 1000, 10000 items; `DataType` Small,
Medium, Large.

### `realworld`: RealWorldScenarioBenchmarks

Application-shaped workloads: a user dashboard, a product catalog, an API gateway, an offline-first mobile app, a
real-time app with frequent updates and analytics reports. Parameters: `UserCount` 50, 100, 200; `ProductCount` 100,
500, 1000.

### `generic`: GenericInterfaceBenchmarks

A generic repository with different entity types, mixed operations, invalidation per type, generic methods with
constraints and concurrent access. Parameter: `ItemCount` 50, 100, 200.

### `serialization`: SerializationBenchmarks

Cache key generators compared: `MessagePackKeyGenerator` (from `MethodCache.Serialization.MessagePack`), a JSON key
generator and a `ToString` key generator, on simple and complex arguments and on key collisions. Parameters:
`ObjectCount` 10, 100, 1000; `ObjectType` Small, Medium, Large.

## Adding a benchmark

Follow the existing naming, add `[MemoryDiagnoser]`, include a baseline where one makes sense, and add the class to
`Program.cs` and to the category table above. Run the setup checks afterwards.

The memory benchmark no-caching baseline calls the uncached data source directly, so warmup and repeated invocations cannot turn it into a cache-hit benchmark.
