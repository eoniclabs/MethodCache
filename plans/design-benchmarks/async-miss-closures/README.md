# Avoid unused closures on asynchronous memory-only misses

The complete cumulative study found Task/ValueTask misses allocating 1128/1136 B,
48 B above their original 1080/1088 B baselines. The two new regression cases use
the actual generated Guid benchmark and first failed on the uncorrected runtime
(`red.log`); after the correction both pass (`green.log`). The assertion counts
origin calls so repeated hits cannot satisfy the allocation budget. An 8 B/op
bookkeeping allowance remains below either unused 24 B display class; the exact
zero-allocation hit checks are unchanged.

`LoadAsync` captured its factory for a fail-safe lambda even when that branch was
inactive. `WriteDistributedAsync` captured a flight for an L2 invalidation
predicate before discovering there was no L2. Put each capture behind its actual
branch. The L2 predicate, invalidation checks and background scope remain intact.

A separate diagnostic over 500,000 synchronous-completing Task misses reports
1128 B/op before and 1080 B/op after (`current.log`, `after-closures.log`). Runtime
allocation event samples support the diagnosis; they are not exact object counts
or timing measurements. `current-layout.log` records the captured fields. The
frozen probe source/project are included for reproducing the diagnostic under
`artifacts/alloc-probe`; its delivery delay is outside the measured allocation
loop and is not a behavioral test.

Validation: 1043 Core tests pass, with one intentional opt-in soak skip; 115
benchmark tests pass. Core coverage executes all four changed executable lines.
The full Core run covers active fail-safe/background scopes and distributed
publication as well as memory-only calls. Logs and exact hashes are retained in
`manifest.json`; normalization changes paths/whitespace only.

This is local correctness/allocation evidence. Three alternating timing pairs
and final candidate CI remain necessary; this correction does not waive the
other failures in the cumulative study.
