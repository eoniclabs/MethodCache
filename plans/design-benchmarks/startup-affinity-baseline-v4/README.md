# Completed nine-launch startup-affinity baseline precision study

This is the older **int-key baseline only**, not a baseline/change comparison and not GUID
or HTTP evidence. All nine predeclared independent launches complete with four cases each,
twelve actual iterations and empty interference guards. The host and all four executed
benchmark children have actual OS CPU2 samples in every launch. No BDN affinity override is used.

| Case | Mean ns | 95% CI | Relative half-width | B/op, every run |
|---|---:|---:|---:|---:|
| SyncHit | 38.845 | 38.093–39.598 | 1.94% | 0 |
| GenericHit | 55.831 | 53.578–58.083 | 4.03% | 0 |
| TaskHit | 41.152 | 40.660–41.644 | 1.20% | 0 |
| ValueTaskHit | 47.272 | 44.472–50.072 | 5.92% | 0 |

Intervals use Student t with df=8 over every independent process mean. SyncHit and TaskHit
have narrower intervals, while GenericHit and ValueTaskHit remain uncertain even after nine
launches. Startup affinity and more samples therefore do not automatically solve every case.
There is no change comparator, speedup claim, precision waiver or complete section-10 pass.
All means and allocation samples remain visible in `baseline-analysis.json`.

The original controller completes at 07:12:02 UTC on 2026-10-16. Source, exact runtime,
SDK/CPU/GC, startup probe, all metadata, process affinity receipts and reports/logs are retained.
Original helper hashes and all collected file hashes are checked before text normalization;
`files-sha256.json` keeps both forms. Earlier failed/pilot cohorts remain separate, as do the
new GUID full matrix and supplementary HTTP comparison. No processes are restarted.
