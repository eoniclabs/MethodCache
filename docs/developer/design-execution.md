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
| PR 6 | codex/http-core-store-v1 / #129 | Latest f793d0a forwards the final strict telemetry/null-key fixtures; local net10 Core passes 1,023 facts with one intentional soak skip. Previous 0dae335 passes all Linux jobs and Windows; macOS fails the old net9 telemetry allocation probe. Original failure is retained. Latest-head CI and complete HTTP/L2 timing remain required. |
| .NET 10 only | codex/net10-only-v1 / #130 | Latest 1b43211 merges #129's fixture follow-up; local Core passes 1,023 facts with one intentional soak skip. Previous befb50a passes all three platforms. Current-head CI and performance acceptance remain required. |
| PR 7, standalone | codex/design-final-gates-v1 / #131 | Exact head 9767fec passes all 14 jobs across Linux/macOS/Windows: 2,519 Linux facts, zero failures, one intentional soak skip, required native backends, properties/soak/cultures, AOT172, packages and docs. Shipped source matches 12bf99d. This excludes constructor/SipHash changes and does not pass section 10. |
| In-place constructor | claude/faster-cache-hits-xft398 / #132 | Head 2b3cb93 includes #131 fixtures and passes all three platforms, including all 14 jobs and 2,519 Linux facts. Ready for review. Full performance acceptance remains required. |
| Prefix hash, final | claude/faster-cache-hits-xft398-siphash / #133 | SipHash-1-3 at 83b92c6 is ready for review and selected in the design notes. All 14 CI jobs and three platforms pass: 2,520 Linux facts, 91 randomized properties, AOT172 and packages. Author-reported three-run medians are preliminary; independent-run means/95% intervals and the complete section-10 gate remain required. |
| PR 7, combined | codex/design-combined-gates-v1 / #134 | Head 3700ade repairs component-path detection and verifies identical setup sources against PR5, Marvin, constructor and SipHash. Local verification: 92 benchmark facts, 1,036 Core facts (one intentional soak skip), 31 Python tests and benchmark build pass. All 14 jobs and three-platform CI pass at 3700ade: 2,521 Linux facts and 91 random properties. Later workload-inventory changes, final GUID sources and section-10 timing remain required. |

## Remaining performance work

PR135 at 0c09fc1 updates the common workloads to GUID identifiers and retains both int/GUID README
comparisons. The combined integration passes 94 benchmark tests and identical component setup checks
on PR5, Marvin, constructor and SipHash. Verify actual generated hit/miss origin counts and rebuild
the same final key types, data and workload sources on every compared revision before freezing the
cohort. Existing int diagnostics remain separate; changing a key type cannot be presented as a
before/after optimization result. GUID sources are not frozen yet.

Complete all adjacent behavior-change comparisons and the cumulative f43854d comparison, plus prefix
components and the local/native L2 workloads specified in section 10. Use three independent alternating
launches, reviewed 95% intervals over run means and unchanged hit-allocation budgets. Wide intervals
remain insufficient evidence. The HTTP 304 regression has not yet been cleared by the fixed confirmation
series; smaller allocations alone do not establish its timing acceptance.

The authoritative receipts and remaining failures are in [design-final-validation.md](design-final-validation.md).
The standalone runtime, constructor candidate and combined SipHash runtime have separate proof scopes.
A component/setup pass does not establish full-path timing or complete functional CI.

Each implementation uses an isolated worktree and is stacked on its preceding dependency. CI runs
automatically after publication. Timing batches use the approved exclusive capacity lease; do not
interrupt unrelated or active CI to obtain a quiet machine. Preserve original failures and frozen ABI
fixtures. Record red-before-green evidence for bugs and keep this tracker current at each publication.
