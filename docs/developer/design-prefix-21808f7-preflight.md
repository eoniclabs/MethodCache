# Prefix candidate 21808f7: inconclusive timing preflight

Before: `2f4a91d0c9144dd0cd9de3482291f365c4443508`. Candidate: `21808f7eef0233213cd12934562104ef92892d9b`.

Three independent BenchmarkDotNet launches per revision, pair order base/change, change/base, base/change. Same macOS arm64 host, .NET 10, workstation/concurrent GC, no telemetry listeners; 16 warmups, 12 measured iterations, 500 ms target iteration time. The ordinary generated-contract workloads and input data are identical.

Intervals below are 95% Student t intervals of the three run means (df=2, t=4.30265). Intervals are too wide for an acceptance decision. No case meets the disjoint-interval failure rule, but wide intervals are explicitly insufficient under section 10; this is not a performance pass. Allocations stayed unchanged except fallback key materialization, which fell from 408 B to 320 B.

| Workload | Base mean [95% CI], ns | Candidate mean [95% CI], ns | Change | Gate |
|---|---:|---:|---:|---|
| SyncHit | 33.21 [30.73, 35.69] | 32.90 [30.56, 35.24] | -0.9% | review uncertainty |
| GenericHit | 48.30 [43.79, 52.80] | 43.82 [41.20, 46.43] | -9.3% | review uncertainty |
| TaskHit | 33.91 [31.39, 36.44] | 33.35 [31.79, 34.92] | -1.7% | review uncertainty |
| ValueTaskHit | 36.99 [35.58, 38.40] | 39.32 [33.45, 45.19] | +6.3% | review uncertainty |
| RecordHit | 79.61 [76.52, 82.71] | 81.60 [80.11, 83.09] | +2.5% | review uncertainty |
| CollectionHit | 65.78 [60.07, 71.50] | 65.00 [64.31, 65.69] | -1.2% | review uncertainty |
| FallbackHit | 218.61 [212.83, 224.39] | 229.84 [214.46, 245.22] | +5.1% | review uncertainty |
| SyncMiss | 406.12 [402.86, 409.38] | 416.50 [415.13, 417.87] | +2.6% | review uncertainty |
| ValueTaskMiss | 484.79 [444.43, 525.15] | 475.97 [425.51, 526.43] | -1.8% | review uncertainty |
| TaskMiss | 459.19 [446.07, 472.32] | 467.75 [429.32, 506.18] | +1.9% | review uncertainty |

Isolated runs and further profiling are required, especially for ValueTask and fallback workloads. The functional acceptance suite remains useful, but does not excuse a timing regression. Raw per-iteration output and CSV reports are retained locally under `/tmp/methodcache-prefix-final-preflight`; metadata.json records the launches, full revisions and SDK.
