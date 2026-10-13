# MethodCache cache access paths

The benchmark adapters exercise current APIs through different paths. They do not represent different versions
of MethodCache.

| Path | Implementation | Work performed |
|---|---|---|
| Generated decorator | `MethodCacheSourceGenAdapter` | Calls cached interface methods through a generated decorator for get-or-create; ordinary adapter lookup uses the shared storage key |
| Manual key | `DirectApiMethodCacheAdapter` | Uses the caller's key directly with the memory cache |
| Runtime key | `RuntimeKeyMethodCacheAdapter` | Builds a runtime policy and generates the storage key on each operation |
| Precomputed prefix | `StaticKeyMethodCacheAdapter` | Generates a prefix during setup and allocates a combined prefix/key string per operation |

`UnifiedCacheComparisonBenchmarks` runs the generated, manual-key and runtime-key adapters. Its methods are named
`MethodCache_SourceGen_*`, `MethodCache_ManualKey_*` and `MethodCache_RuntimeKey_*`. The precomputed-prefix adapter
has contract tests but is not a row in that suite. Its manual get-or-create sequence does not coordinate
concurrent factories and is not a substitute for a generated decorator.

Use cache attributes and normal DI registration for application methods. Manual caching is available when the
application needs direct storage access; see [manual caching](../../docs/features/manual-caching.md).
Runtime key generation remains an active API and its benchmark name describes the measured work.

For the published comparison, run `readme`, which calls generated application methods alongside the other
libraries' cache APIs. Measured numbers and their environment are in [docs/benchmarks.md](../../docs/benchmarks.md).
See the [benchmarking guide](README.md) for commands.
