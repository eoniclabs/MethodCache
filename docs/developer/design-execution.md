# Design implementation tracking

Goal: finish the complete pre-1.0 design in [design-spec.md](design-spec.md), including all acceptance tests,
performance gates and reviewable stacked pull requests. Authorized by Johan on 2026-10-15 to execute the
remaining series continuously. Package publication and merge are separate from this implementation goal.

The final supported application target is .NET 10. Earlier net8/net9 matrix counts in the individual
validation reports remain evidence for their historical revisions, not additional supported targets.
Each status below describes an exact GitHub head; receipts retain the verification timestamps.
A passing ancestor does not establish the latest head's result.

| Step | Branch / PR | State and remaining work |
|---|---|---|
| PR 0 | codex/design-spec-v1 | Specification complete; canonical copy is included in PR 1. |
| PR 1 | codex/generated-contract-v1 / #123 | G01–G14 implementation and frozen ABI fixtures complete. Head 8abc020 passes all three platforms. Preserve frozen fixtures; full section-10 obligations remain in the final cohort. |
| PR 2 | codex/user-api-policy-v1 / #124 | Head e368484 passes all three platforms. Policy state/fault acceptance also runs against the final model, Redis and SQL descendants. See design-pr2-validation.md. Broad section-10 gate, including L2 read round trips, remains required. |
| PR 3 | codex/keys-attributes-v1 / #125 | K01–K08 implemented; head a8ad8a5 passes all three platforms. Parser/key/culture/AOT/package evidence is in design-pr3-validation.md. Section-10 timing remains required. |
| PR 4 | codex/transport-format-v1 / #126 | D01–D10 implemented; head 399e165 passes all three platforms. Native fault/state, format, retention and package evidence remains in the validation reports. Section-10 timing remains required. |
| PR 5 | codex/core-telemetry-v1 / #127 | T01–T05 implemented; head 2f4a91d passes all three platforms. Listener/privacy/health coverage is retained. Timing gates remain required. |
| Prefix hash | codex/prefix-hash-v1 / #128 | Marvin head 9c31e3b passes all three platforms; standalone runtime source 850cef8 has its own full functional receipt. Final algorithm is SipHash in #133; retain this revision as a separate comparison point. |
| PR 6 | codex/http-core-store-v1 / #129 | Current f0dd6c2 optimizes 304 header copying after the earlier response-reuse candidate failed timing. All four current commit statuses pass. Its six-launch comparison has four insufficient intervals; the separate fixed eighteen-launch CPU2 comparison also completes with four insufficient intervals. Full section-10 acceptance remains required. |
| .NET 10 only | codex/net10-only-v1 / #130 | Current 1bafff1 merges #129's header optimization and retains the .NET 10 minimum. All four current commit statuses pass; performance acceptance remains required. |
| PR 7, standalone | codex/design-final-gates-v1 / #131 | Exact head 9767fec passes all 14 jobs across Linux/macOS/Windows: 2,519 Linux facts, zero failures, one intentional soak skip, required native backends, properties/soak/cultures, AOT172, packages and docs. Shipped source matches 12bf99d. This excludes constructor/SipHash changes and does not pass section 10. |
| In-place constructor | claude/faster-cache-hits-xft398 / #132 | Head 2b3cb93 includes #131 fixtures and passes all three platforms, including all 14 jobs and 2,519 Linux facts. Ready for review. Full performance acceptance remains required. |
| Prefix hash, final | claude/faster-cache-hits-xft398-siphash / #133 | Current 4f6cd73 passes all four commit statuses. The exact 83b92c6 functional receipt retains all 14 jobs, 2,520 Linux facts, 91 randomized properties, AOT172 and packages. Author-reported medians remain preliminary; independent-run means/95% intervals and the complete section-10 gate remain required. |
| GUID workloads | claude/faster-cache-hits-xft398-guid-bench / #135 | Exact unchanged head 419c786 now passes all fourteen jobs/four statuses after a deliberate retry with the repaired coordinator: 2,522 Linux facts, one intentional soak skip, AOT172, required backends/packages and 94.0% changed-line coverage. The historical macOS checkout failure remains archived. PR136 is integrated and installed atomically from 388050f, and was separately merged by Johan. |
| PR 7, combined | codex/design-combined-gates-v1 / #134 | Combined review against main with history preserved. Exact7b passes all14jobs/fourstatuses:2639Linux/0failures/1intentionalsoakskip,diff94.3%. Johan approved five exact HTTP caps; canonical replay passes all53existinglocalCPUcases plus6affectedprefixmisses and all67costcases (6unchangedacceptedcosts). Original failures/wide reports retained. Approval publication needs final exact-head CI; no merge/release. |

## Remaining verification

Johan approved alternative 1 on2026-10-17. The five exact HTTP caps are now active;
all other timing/cost/correctness/precision rules remain unchanged. Reproducible replay
covers59wholeCPUcases,includingall53existinglocalcases,andall67costcases. No new
measurement or cross-study pooling is used. Original Mac14wide intervals stay failed;
complete precise Nimbus2cases provide separate whole-case confirmation.
[Approved final performance acceptance](../../plans/design-benchmarks/http-time-acceptance-v1/).

The preceding exact7b publication passes all14jobs/fourstatuses. The only remaining
execution step is publishing the approved rules/evidence/docs and verifying actual
final-head CI and the76row acceptance audit. Merge,release and PublicAPI promotion
remain separate. PR138 stays a separate follow-up, outside this completed design runtime.

## Historical verification before approval

The latest6a2cc002 publication passes every required CI job. The complete targeted
Nimbus confirmation is terminal:40launches,2slower,0wide,0executionerrors. It resolves
the two previous precision gaps with actual regressions, preserving the original
Mac report. Five precise HTTP regressions remain. The new five-casev2bounds are
inactive pending a design decision; they change no precision/cost/correctness rule.
No benchmark,CIjob or collection process remains active for these completed runs.

Latest published c9ec0db3 correctness/build verification is complete on all three
platforms; its raw evidence is in [combined-functional-c9ec0db](../../plans/design-benchmarks/combined-functional-c9ec0db/).
The current candidate passes cumulative costs; complete fourteen-case timing now
fails with three slow cases and two wide intervals (nine cases pass). All280launches
and original/current-rule reviews are retained in [current HTTP timing](../../plans/design-benchmarks/http-client-index-final14-v1/). Older source-point results below retain their
actual scope; earlier green CI is not used to validate later code.

The historical180launch multi-PR BenchmarkDotNet matrix is **cancelled** by Johan's
2026-10-16 decision. It must not be restarted. Section10 now requires a complete
cost profile per behavior change and a cumulative paired-time gate, with filtered
comparisons for changes that otherwise only cost CPU. The completed historical
reports remain evidence for their actual source points; failures are not relabelled.

The current5257fb63 source-equivalent runtime passes the full67case cumulative cost
profile on both revisions: zero execution errors, zero failing costs, six unchanged
accepted costs. Byte/Redis/SQL bounds remain in the canonical accepted.json.
The current PR134 direct-base cost comparison against419c786 also passes all67cases
with zero failures and zero accepted exceptions; this is deterministic reuse of
matching complete cost rows, not a timing comparison.
Exact published5257fb63 now passes all fourteen jobs/four statuses, including real
backends, randomized properties, enabled soak, cultures, AOT175, packages and docs.

Completed cumulative timing remains under the unchanged strict rules. Non-HTTP
confirmations and scoped F05 generated-prefix acceptance have their own source and
scope receipts. Current diagnostic HTTP measurements still fail: the publication
cold case ratio is1.226 and the compact-scope listening304case ratio is1.049.
All original launch records, precision failures and source/build receipts are retained.
The proposed exact HTTP limits are **inactive** and establish no accepted result.

The exact5257fb63 whole fourteen-case HTTP confirmation is complete and fails:
four cold cases are20.6–23.8%slower,one listening304case is3.7%slower and one listening304
interval is too wide. Four cases are faster and four unchanged. All280launches and
both complete production Git inventories verify; no proposed limit is applied.
The current-source filtered ten-case non-HTTP confirmation passes all200launches,
with all ten affected enabled-origin cases faster and precise. Earlier results keep
their own source points; the macOS tmp-path collector repair changes no measurements.

The client-index correction from the isolated trial is now adopted in the working
combined tree, with exactly two changed production files and unchanged metadata layout.
It preserves physical removal, full tags/generations and URL/custom indexes; client-tag
invalidation scans resident HTTP entries. Targeted22/fullHTTP345/Core1055pass (one
intentional opt-in skip), and cumulative67case costs pass with six unchanged accepted
costs. The complete two-case direct pilot against5257fb63 is precise and faster:
coldmiss7.2%,listening3043.4%. This is not a cumulative time gate. Fresh fourteen-case
candidate timing againstf438 is complete and fails:3slower/2wide/9pass,0executionerrors. Actual published c9ec0db3 CI now
passes all fourteen jobs as recorded above.

The complete per-change cost archive retains seventeen overages on five historical
heads (#124,#129,#130,#132,#133). Those particular standalone heads do not pass their
cost gates. Johan approved combined review in #134 against main on2026-10-17, preserving
commit history and replacing separate historical-head cost acceptance for this series.
The combined corrections and final cumulative gates remain required; no historical
verdict changes. No merge or package publication is part of this
implementation task's standing authorization.

The authoritative evidence and outstanding gates are in
[design-final-validation.md](design-final-validation.md). The76row inventory separates
functional assertions, scoped prefix evidence and the still-open cumulative timing
and exact-final-head requirements.

Historical matrix references, retained for provenance only:
[V4 rejected preparation](../../plans/design-benchmarks/guid-full-preparation-v4/),
[V5 preparation and native receipts](../../plans/design-benchmarks/guid-full-preparation-v5/),
[V6 source-scoped transition](../../plans/design-benchmarks/focused-schedule-v6/),
[complete per-change cost verdicts](../../plans/design-benchmarks/per-change-cost-v1/).

Each implementation uses an isolated worktree and is stacked on its preceding dependency. CI runs
automatically after publication. Timing batches use the approved exclusive capacity lease; do not
interrupt unrelated or active CI to obtain a quiet machine. Preserve original failures and frozen ABI
fixtures. Record red-before-green evidence for bugs and keep this tracker current at each publication.
