> Reopened after PR review, 2026-10-17. The previous completion checkpoint is historical.
> Runtime corrections, a reviewed pre-release contract freeze, a complete independent PR self-review,
> fresh cost/time evidence and final CI are required. Johan requires the self-review before any new CI run.

# Design implementation tracking

All implementation steps in [design-spec.md](design-spec.md) are delivered together
in [PR #134](https://github.com/eoniclabs/MethodCache/pull/134), reviewed against main
under Johan's approved combined arrangement. The original code commit ordering is
preserved; raw measurement paths are removed from the PR history. Original measured
SHA references remain available in the separate source-point Git bundle.

| Step | Original PR | Outcome |
|---|---|---|
| PR 0 | Specification in #123 | Specification and acceptance scenarios complete. |
| PR 1 | #123 | Frozen generated contract, snapshots, binding and vendor fixtures. |
| PR 2 | #124 | User API, policy identities/overrides and invalidation results. |
| PR 3 | #125 | Keys, attribute syntax, duration parsing and diagnostics. |
| PR 4 | #126 | Owned transport/providers, wire format and recovery. |
| PR 5 | #127 | Core telemetry, statistics and health reporting. |
| Prefix hash | #128, #132, #133 | In-place key construction and final SipHash prefix/node reuse. |
| PR 6 | #129 | HTTP storage in Core, generations, budgets, cookies and admin. |
| .NET 10 | #130 | Libraries/tests require .NET 10; frozen vendor fixtures unchanged. |
| Workloads | #135 | GUID workloads and explicit int controls. |
| L2 correction | #137 | Merged into combined branch with its merge commit retained. |
| PR 7 | #131, #134 | All 76 acceptance requirements verified; approved time costs recorded. |

The final pre-cleanup checkpoint `a08c279acf5f2a00859bfd08e64797ac7c8e1170` passes
all 14 CI jobs/four statuses. Its 2,639 Linux tests have zero failures and one
intentional opt-in soak skip; changed-line coverage is 94.2%. Exact cleanup-head CI
remains the publication gate. [Final verification](design-final-validation.md).

Johan approved five exact HTTP time caps on 2026-10-17. Canonical performance review
covers 59 complete CPU cases (five accepted costs, no unaccepted slowdowns or wide
intervals) and 67 cost cases (six existing accepted costs). Original failures and
historical per-head overages remain preserved externally.
[Performance and external archive](design-performance-results.md).

The historical 180-launch matrix is cancelled and must not restart. Future results
belong under ignored artifacts/ or outside the checkout. No raw logs, measurements,
profiles or generated evidence catalogues are committed. No merge/release/PublicAPI
promotion or PR138 integration is included in this implementation task.
