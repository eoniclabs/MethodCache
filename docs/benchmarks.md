[← MethodCache documentation](README.md)

# Benchmarks

## x64 cache hits (2026-10-18)

The README table. Warm, local hits through each library's native API, measured on baloo, a desktop
used as the project's timing machine. The workload, payload and settings are the same as in the
Arm64 run below; see there for what is measured and why the payload is marked immutable.

| Approach | `int` ns ± error | Allocated | `Guid` ns ± error | Allocated |
|---|---:|---:|---:|---:|
| IMemoryCache by hand | 54.2 ± 0.1 | 112 B | 72.1 ± 1.0 | 176 B |
| MethodCache `[Cache]` | 49.2 ± 0.2 | 0 B | 62.0 ± 0.3 | 0 B |
| HybridCache | 63.5 ± 0.4 | 104 B | 77.2 ± 0.7 | 168 B |
| FusionCache | 491.2 ± 7.6 | 344 B | 527.6 ± 3.3 | 416 B |

Values are BenchmarkDotNet means; the error is half its 99.9% confidence interval, in nanoseconds.
Three independent processes per case, ten warmup and fifteen actual iterations per process, fixed at
4,194,304 invocations. The machine was otherwise idle and no build ran during the measurement.
MethodCache is 9% faster than the hand-written IMemoryCache hit for integers and 14% for GUIDs,
with no managed allocation.

The same command on the same machine at f43854d, before the design series and the hit-path work,
measured the `int` hit at 55.7 ns against IMemoryCache's 54.6 ns (1.02x). That commit has no GUID case,
and its HybridCache number used the unannotated payload, so only the MethodCache/IMemoryCache pair compares.

```text
Source: 291ed9e28ea5eb254f86ba57b964e4033c26fd87
BenchmarkDotNet 0.15.4; baloo, AMD Ryzen 7 5800X, 8 cores / 16 threads
Windows 11 (10.0.26300); .NET 10.0.13, X64 RyuJIT x86-64-v3; SDK 10.0.402
Workstation GC; LaunchCount=3; WarmupCount=10; IterationCount=15
InvocationCount=4194304; UnrollFactor=16
```

The command is the one under the Arm64 run below. Raw reports remain outside Git.

## Arm64 cache hits (Apple M5, 2026-10-18)

These are warm, local hits through each library's native API, including key construction and the return
value: `Task<BenchUser<TId>>` for MethodCache and the hand-written IMemoryCache wrapper, native
`ValueTask<BenchUser<TId>>` for HybridCache and FusionCache. Both `int` and GUID ids are measured.
Provider options use their defaults, including MethodCache's built-in counters; each entry has a ten-minute TTL.

The shared payload is a sealed, immutable record. It now explicitly declares `[ImmutableObject(true)]`:
without that hint HybridCache deserialized a new object on each hit, while the other providers reused theirs.
A new identity guard failed for that old setup and passes for all eight hit paths with the hint.
This follows [Microsoft's object-reuse guidance](https://learn.microsoft.com/aspnet/core/performance/caching/hybrid?view=aspnetcore-10.0#reuse-objects).
The older cloud table below used the unannotated payload, so its HybridCache work differs from this comparison.

| Approach | `int` ns ± error | Allocated | `Guid` ns ± error | Allocated |
|---|---:|---:|---:|---:|
| IMemoryCache by hand | 29.0 ± 0.1 | 112 B | 36.4 ± 0.3 | 176 B |
| MethodCache `[Cache]` | 36.0 ± 0.6 | 0 B | 44.9 ± 1.6 | 0 B |
| HybridCache | 33.3 ± 0.1 | 104 B | 44.4 ± 0.1 | 168 B |
| FusionCache | 127.9 ± 0.5 | 344 B | 134.9 ± 0.4 | 416 B |

Values are BenchmarkDotNet means; the error is half its 99.9% confidence interval, in nanoseconds.
The complete matrix uses three independent processes per case, ten warmup and fifteen actual iterations
per process, fixed at 4,194,304 invocations. All 360 actual measurements are retained and every actual
iteration exceeds 100 ms. The reported statistics use BenchmarkDotNet's default upper-outlier handling.
No library, process or pair is selected for a favorable result.

MethodCache is roughly 23–24% slower than the hand-written IMemoryCache hit on this machine, with no
managed allocation. It is close to HybridCache for GUIDs and slower for integers. FusionCache's measured
call paths take about 3–3.6 times as long as MethodCache's. These numbers concern
these native API hit paths; they are not miss, contention, L2, safety-feature or raw storage-engine comparisons.

MethodCache's launch-to-launch variation was larger than the other libraries', and BenchmarkDotNet
flagged one MethodCache distribution as bimodal. Small differences from HybridCache should be treated
as approximate. The process ran with normal scheduling: BenchmarkDotNet could not elevate its priority;
no forced CPU affinity was used. This is an interactive workstation, not a dedicated benchmark host.

```text
Source: 00aaef9841b608b099ea564bdd9566907e718d40 (runtime src tree unchanged from main 1ca7e277)
BenchmarkDotNet 0.15.4; Megatron, Apple M5, 10 physical/logical cores
macOS 27.0.1 (26A434); .NET 10.0.3, native Arm64 RyuJIT; SDK 10.0.103
Concurrent workstation GC; LaunchCount=3; WarmupCount=10; IterationCount=15
InvocationCount=4194304; UnrollFactor=16
Microsoft.Extensions.Caching.Memory 9.0.9
Microsoft.Extensions.Caching.Hybrid 9.9.0
ZiggyCreatures.FusionCache 2.0.0
```

These are the versions pinned by the benchmark project, not a claim to have tested the latest release
of every library. MethodCache is compiled from source at the SHA above.

```bash
dotnet run -c Release --project benchmarks/MethodCache.Benchmarks -- readme --filter '*Hit' \
  --launchCount 3 --warmupCount 10 --iterationCount 15 \
  --invocationCount 4194304 --unrollFactor 16 --exporters JSON
```

Raw reports and logs remain outside Git under
`/Users/johan/dev/MethodCache-artifacts/postdesign-comparison-2026-10-18/readme-confirmation/`.
The first, complete adaptive-invocation study is also preserved there under `readme-comparison/`.
It measured MethodCache at 34.9/42.8 ns for int/GUID but produced short-iteration warnings for those
cases; the entire eight-case matrix was rerun with fixed invocation counts for the published table.
The two studies are not pooled. Design-series acceptance remains a separate paired baseline comparison;
see [the verification record](developer/design-final-validation.md).

## Historical cloud run (2026-10-15)

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
