# Complete per-change cost evidence

All 12 actual PR edges execute all 67 cases on both sides without execution
errors. All 1,608 raw records, original reports, exact revisions, pinned runtime
and backend images, SDK/CPU receipts, common harness and recursive checksums for
4,200 build/runtime files are retained. All 130 original archive hashes verify.
Independent reviews with the current limits reproduce every original verdict.
The two later sync/int time rules cannot waive byte or command costs.

Seven profiles pass: #123, #125, #126, #127, #128, #134 and #137. Five historical
heads fail: #124 (eight overages), #129 (six), and #130/#132/#133 (one inherited
Redis fresh-hit write violation each). These 17 failures remain explicit; later
corrections do not turn their original reports into passes. In particular #137
removes those Redis writes. The current combined runtime passes its independent
cumulative cost profile too. Worker success is separate from the cost verdict.

[Exact edges, verdicts and failure details](summary.json). This archive is cost
evidence only; no time acceptance is inferred.
