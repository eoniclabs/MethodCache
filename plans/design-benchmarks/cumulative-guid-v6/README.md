# Complete cumulative GUID comparison: failed acceptance

All eighteen preregistered cumulative family launches complete: original
`f43854dbcd3646893f490e78a0a1d9651e9c4521` versus final frozen runtime
`b92553dac51d0a29f1784508ca4cbacedeea73ea`. Each endpoint has three independent
rounds of 27 contract, 20 pressure and 16 native cases: 378 individual case
processes in total. The fixed V6 schedule and original V5 measurement settings,
actual host/child affinities, empty interference guards and pinned native
images all pass. No sample or process is dropped or pooled with earlier studies.

The unchanged reviewer reports ten decisive comparable timing regressions and
six corresponding L2-hit allocation increases. Most remaining intervals are too
broad to establish acceptance. Eight cases with explicitly changed work remain
separate. The historical PR comparisons continue; this result does not identify
which individual PR introduced every cost.

| Comparable workload | Original mean | Final mean | Change |
|---|---:|---:|---:|
| Task miss | 791.43 ns | 898.12 ns | +13.48% |
| ValueTask miss | 806.80 ns | 937.07 ns | +16.15% |
| Cold HTTP miss, four listener/invalidation variants | 7.16–7.21 µs | 11.28–11.61 µs | +56.41–61.86% |
| Redis HTTP fresh | 167.89 µs | 222.54 µs | +32.55% |
| SQL Server HTTP fresh | 0.4153 ms | 13.6002 ms | +3174.49% |
| Redis HTTP miss | 322.62 µs | 405.14 µs | +25.58% |
| SQL Server HTTP miss | 25.4062 ms | 26.7435 ms | +5.26% |

These timing intervals are disjoint in the direction of regression and exceed
3%. Exact process means, 95% Student-t intervals and allocations are retained in
`focused-schedule-v6/cumulative-analysis-20261016-1354.json`. They stop acceptance;
new correctness features and lower allocations elsewhere do not waive them.

All six L2-hit allocation increases are retained: Redis method reads in both
round-trip modes, SQL method reads in both modes, and fresh HTTP responses on
both providers. Simple generated hit allocations remain zero; generic, collection
and fallback allocations decrease. Their timing intervals remain insufficient,
so no full-path speedup is claimed. Miss allocations also rise, including 48 B
for Task/ValueTask misses; those do not disappear merely because the hard
allocation budget applies to hits.

The 127 original evidence files include all eighteen full reports, complete
logs, affinity/interference/lease receipts, six native image receipts, frozen
selection/controller/reviewer, launch transition and a scoped source/input index.
Their original hashes were independently verified after retrieval. `manifest.json`
records both original and normalized hashes. The absolute remote benchmark root
is replaced, line endings are normalized and trailing whitespace is removed.
Full prepared input provenance remains in the previously
published V5 preparation/protocol. This archive is preserved before corrections;
later candidates require their own measurements and cannot replace these results.
