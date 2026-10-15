# Prefix candidate preflight: timing regression

Before: `2f4a91d0c9144dd0cd9de3482291f365c4443508`. Candidate: `a3cbcad9c50358da7e426748d580f8da251c4210`.

Three independent BenchmarkDotNet launches per revision, pair order base/change, change/base, base/change. Same macOS arm64 host, .NET 10, workstation/concurrent GC, no telemetry listeners; 6 warmups, 15 measured iterations, 200 ms target iteration time. The ordinary generated-contract workloads and input data are identical.

Intervals below are 95% Student t intervals of the three run means (df=2, t=4.30265). This is a failed preflight, not the complete section-10 matrix or a performance pass. Allocations were unchanged in all workloads; the timing gate still applies.

| Workload | Base mean [95% CI], ns | Candidate mean [95% CI], ns | Change | Gate |
|---|---:|---:|---:|---|
| SyncHit | 34.76 [28.94, 40.58] | 32.84 [31.98, 33.69] | -5.5% | review uncertainty |
| GenericHit | 48.10 [41.37, 54.83] | 43.66 [43.15, 44.17] | -9.2% | review uncertainty |
| TaskHit | 34.10 [32.71, 35.49] | 32.67 [31.33, 34.01] | -4.2% | review uncertainty |
| ValueTaskHit | 35.99 [34.40, 37.58] | 34.95 [33.33, 36.57] | -2.9% | review uncertainty |
| RecordHit | 77.79 [75.75, 79.84] | 82.08 [78.42, 85.75] | +5.5% | review uncertainty |
| CollectionHit | 64.57 [62.56, 66.58] | 64.38 [62.39, 66.36] | -0.3% | review uncertainty |
| FallbackHit | 217.80 [207.86, 227.75] | 262.85 [240.13, 285.57] | +20.7% | FAIL |
| SyncMiss | 406.49 [404.88, 408.10] | 418.06 [414.84, 421.28] | +2.8% | review uncertainty |
| ValueTaskMiss | 462.32 [459.79, 464.86] | 484.57 [473.82, 495.31] | +4.8% | FAIL |
| TaskMiss | 450.86 [449.05, 452.67] | 471.00 [462.55, 479.45] | +4.5% | FAIL |

The candidate must be revised and rerun. The functional acceptance suite remains useful, but does not excuse a timing regression. Raw per-iteration output and CSV reports are retained locally under `/tmp/methodcache-prefix-preflight`; metadata.json records the launches, full revisions and SDK.
