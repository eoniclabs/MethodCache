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
| PR 7, combined | codex/design-combined-gates-v1 / #134 | Checkpoint 453be95 passes all fourteen jobs/four statuses: 2,562 Linux facts, one intentional soak skip, required backends, properties/soak/cultures, AOT175, packages/docs and 94.0% changed-line coverage. All 53 benchmark-validation Python guards pass on Nimbus. V5 preparation passes twenty actual generated-program builds, fourteen native setups and sixty compiled-family validations; timing starts at 09:10:18 UTC. Full timing/review and the following evidence batch's own CI remain required. |

## Remaining performance work

The corrected common primary GUID workload is frozen at 453be95; genuine generated prefix C#
remains frozen at bbff4a6. V5 changes only the cold-HTTP iteration boundary outside the measured
call, and the component factory's project filename. Its fourteen runtime revisions remain unchanged. The fourteen runtime points cover the actual
PR merge-base edges, including the constructor's older standalone base and isolated SipHash head.
Primary workloads have 27 local-contract, 20 local-pressure and 16 native cases. Six component points
have 18 lookup, 36 facade/reference and 48 actual generated-interface cases; Int and Guid remain
separately labelled. Matching source/program hashes and setup guards pass locally. Nimbus preparation
and digest-pinned Redis/SQL image validation now pass on Nimbus, together with all sixty
compiled-family preflights and an independent rehash of 2,358 harness DLLs.

The fixed full matrix is 180 bounded launches: fourteen primary points × three families × three rounds,
plus six component points × three families × three rounds. Each comparison uses identical workload
bytes and key types. Existing int diagnostics remain separate; changing a key type cannot be presented
as a before/after optimization result. The full timing dispatcher starts on 2026-10-16 at
08:30:54 UTC after input review, then stops at 08:32:09 before any benchmark executes:
BDN rejects the asynchronous cold-HTTP iteration setup. A regression test fails before its
correction; generated-program compilation is now a separate preflight gate. A fresh cohort,
all 180 successful launches and statistical review remain required.
[Rejected V4 preparation and dispatch evidence](../../plans/design-benchmarks/guid-full-preparation-v4/).

V5 then compiles all twenty actual BDN-generated programs (67 primary cases including four
untimed diagnostics; 102 component cases), passes all fourteen fresh native setups, and validates
all sixty compiled families. Independent review rehashes 2,358 harness and seventeen runner DLLs
before the explicit 09:10:18 UTC dispatch on 2026-10-16. The fixed 180-launch series is running;
no partial run means are assessed.
[Completed V5 preparation, native image receipts and dispatch](../../plans/design-benchmarks/guid-full-preparation-v5/).

Complete all adjacent behavior-change comparisons and the cumulative f43854d comparison, plus prefix
components and the local/native L2 workloads specified in section 10. Use three independent alternating
launches, reviewed 95% intervals over run means and unchanged hit-allocation budgets. Wide intervals
remain insufficient evidence. The completed HTTP response-reuse confirmation fails one comparable
case by 9.36% with disjoint intervals. The completed six-launch header-copy comparison has four
insufficient intervals despite lower allocations. A separate fixed nine-pair startup-affinity
comparison also completes all eighteen launches with four insufficient intervals; no samples
are pooled and no timing pass is inferred. The completed
older nine-launch int baseline still has two broad intervals and supplies no optimization pass.

The authoritative receipts and remaining failures are in [design-final-validation.md](design-final-validation.md).
The standalone runtime, constructor candidate and combined SipHash runtime have separate proof scopes.
A component/setup pass does not establish full-path timing or complete functional CI.

Each implementation uses an isolated worktree and is stacked on its preceding dependency. CI runs
automatically after publication. Timing batches use the approved exclusive capacity lease; do not
interrupt unrelated or active CI to obtain a quiet machine. Preserve original failures and frozen ABI
fixtures. Record red-before-green evidence for bugs and keep this tracker current at each publication.
