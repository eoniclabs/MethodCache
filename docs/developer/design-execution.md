> Updated 2026-10-18: #134 is merged and main passes all fourteen CI jobs.
> The original stacked PRs #123–#131 are closed as integrated. Historical measurements remain historical;
> [current verification](design-final-validation.md) and [performance evidence](design-performance-results.md)
> distinguish completed functional checks from CPU precision requirements.

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
| PR 7 | #131, #134 | Merged in #134; functional CI passes; current CPU evidence is tracked separately. |

The final pre-cleanup checkpoint `a08c279acf5f2a00859bfd08e64797ac7c8e1170` passes
all 14 CI jobs/four statuses. Its 2,639 Linux tests have zero failures and one
intentional opt-in soak skip; changed-line coverage is 94.2%. That historical CI predates the review corrections. Current verification is tracked separately. [Final verification](design-final-validation.md).

Johan approved five exact HTTP time caps on 2026-10-17. Canonical performance review
covers 59 complete CPU cases (five accepted costs, no unaccepted slowdowns or wide
intervals) and 67 cost cases (six existing accepted costs). Original failures and
historical per-head overages remain preserved externally.
[Performance and external archive](design-performance-results.md).

The historical 180-launch matrix is cancelled and must not restart. Future results
belong under ignored artifacts/ or outside the checkout. No raw logs, measurements,
profiles or generated evidence catalogues are committed. No new merge/release/PublicAPI
promotion is included in this follow-up. #134 and PR #138 have since been merged with Johan's approval.
