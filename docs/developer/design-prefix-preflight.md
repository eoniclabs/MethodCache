# Prefix candidate preflight: timing regression

Before: `2f4a91d0c9144dd0cd9de3482291f365c4443508`. Candidate: `06542dfed3b6817dbdff538726eb3f2314289238`.

Three independent BenchmarkDotNet launches per revision, pair order base/change, change/base, base/change. Same macOS arm64 host, .NET 10, workstation/concurrent GC, no telemetry listeners; 6 warmups, 15 measured iterations, 200 ms target iteration time. The ordinary generated-contract workloads and input data are identical.

Intervals below are 95% Student t intervals of the three run means (df=2, t=4.30265). This is a failed preflight, not the complete section-10 matrix or a performance pass. No hit allocation increase was observed.

| Workload | Base mean [95% CI], ns | Candidate mean [95% CI], ns | Change | Gate |
|---|---:|---:|---:|---|
| SyncHit | 32.58 [30.02, 35.14] | 61.05 [58.32, 63.77] | +87.4% | FAIL |
| GenericHit | 46.96 [44.69, 49.22] | 56.00 [55.43, 56.58] | +19.3% | FAIL |
| TaskHit | 33.41 [32.29, 34.52] | 66.11 [63.13, 69.08] | +97.9% | FAIL |
| ValueTaskHit | 36.21 [34.62, 37.80] | 48.53 [45.47, 51.60] | +34.0% | FAIL |
| RecordHit | 77.36 [72.04, 82.68] | 483.89 [483.16, 484.62] | +525.5% | FAIL |
| CollectionHit | 65.19 [62.40, 67.98] | 470.51 [467.70, 473.32] | +621.8% | FAIL |
| FallbackHit | 227.39 [186.17, 268.62] | 726.27 [724.73, 727.81] | +219.4% | FAIL |
| SyncMiss | 405.31 [400.96, 409.67] | 1650.46 [1630.05, 1670.86] | +307.2% | FAIL |
| ValueTaskMiss | 466.44 [453.04, 479.84] | 2005.25 [1941.65, 2068.85] | +329.9% | FAIL |
| TaskMiss | 466.59 [425.40, 507.79] | 1725.34 [1674.80, 1775.87] | +269.8% | FAIL |

The candidate must be revised and rerun. The functional acceptance suite remains useful, but does not excuse a timing regression. Raw per-iteration output and CSV reports are retained locally under `/tmp/methodcache-prefix-preflight`; metadata.json records the launches, full revisions and SDK.
