# HTTP 304 response reuse: one directional pair

Prefix source: `850cef8f3bc709eaf464b3c25e5ec898217fcc73`.
Candidate source: `12bf99d877c33925fe0d0037f45e652d0260d8a4`.
The same three workload source checksums, .NET 10 SDK/runtime, CPU and concurrent
workstation GC are recorded in `metadata.json`. Each revision has four complete cases,
12 actual iterations per case, and an empty external-work interference guard. Exclusive
leases were held separately and released between batches. The original reports/logs
are retained, with repository LF normalization of text line endings.

**This is one pair, not section-10 acceptance.** Means below describe that pair; the
12 iterations are not independent process launches and are not substituted for those
launches in confidence intervals. The candidate reduces allocation but is slower in
this sample. This leaves the earlier confirmed 304 regression unresolved. A fresh fixed
three-pair alternating confirmation now tests the current candidate; no samples from
this directional pair will be pooled into it. Native setup remains a separate queued
check, not an inferred result from local timing. The fresh confirmation controller
started as Nimbus PID 1545763; its frozen protocol is in `confirmation-protocol/`.
It uses the existing verified binaries and records their checksums, without changing
workloads or the 25-minute exclusive lease bound.

| Listener / invalidation | Prefix ns | Candidate ns | Time change | Allocated bytes |
|---|---:|---:|---:|---:|
| Listening=False&AfterInvalidation=False | 5158.44 | 5662.18 | +9.77% | 8256 → 7536 |
| Listening=False&AfterInvalidation=True | 5318.34 | 5461.09 | +2.68% | 8256 → 7536 |
| Listening=True&AfterInvalidation=False | 5172.98 | 5647.61 | +9.18% | 8256 → 7736 |
| Listening=True&AfterInvalidation=True | 5204.14 | 5610.39 | +7.81% | 8256 → 7736 |
