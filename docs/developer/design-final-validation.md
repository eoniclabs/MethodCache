# Design verification status

The complete design series is reviewed together in [PR #134](https://github.com/eoniclabs/MethodCache/pull/134)
against main, retaining source commit history. No raw measurement files remain in the PR history.

The independent review against main `8402ad2f` is complete. All 27 numbered review findings and three inline
findings have code/test corrections; [the review record](design-review-fixes.md) lists them and the additional
admin-id collision found during self-review. Review happened before any new push or full CI run.

## Current verification (2026-10-18)

The series is merged through #134. Current main `1ca7e2776c228c6933fa3e78ed96afb6b9cba98c`
passes all fourteen CI jobs and all four commit statuses on Linux, macOS and Windows. The Linux
suite has 2,740 passed tests, zero failures and one intentional opt-in soak skip. Properties, enabled
soak, both cultures, required Redis/SQL, demo, AOT, package smoke tests and generated documentation pass.

`Contract1Final` freezes 104 Generated declarations and three vendor images from `3e82c06e`;
ordinary builds never regenerate them. #143 changes newly generated value-task hits while preserving
the public method called by the frozen images. The complete independent review predates the new CI runs.

The previously recorded 67-case cost profile has six accepted costs and zero failures. #142 separately
checks all 48 added prefix cases without failing costs. These are allocation/command checks, not CPU evidence.
The merged ASCII-hash and value-task corrections supersede the older GUID/value-task regressions.
Three exact additional time caps were approved on 2026-10-18; they do not waive the 6% precision requirement.
The targeted Megatron record/long-Task confirmations now pass with unchanged rules;
[current performance evidence](design-performance-results.md) records their exact sources, pair counts and intervals.

Original source and CI hashes remain historical evidence in the external archive. The raw-artifact
history cleanup preserves all 536 original source commits and 56 merge commits, with no source/test/build
changes from the cleanup itself. Original #123–#131 are now closed as integrated; #105 and #117 remain open
because they contain distinct changes.

The 76 specification requirements remain grouped as follows; the external review inventory records current
source/test hashes without rewriting historical failed or noisy studies.

| Requirements | Count | Acceptance definition |
|---|---:|---|
| Generated contract and binding G01–G14 | 14 | [PR 1](design-pr1-validation.md) |
| User API and policy P01–P16 | 16 | [PR 2](design-pr2-validation.md) |
| Keys and diagnostics K01–K08 | 8 | [PR 3](design-pr3-validation.md) |
| Transport and format D01–D10 | 10 | [PR 4](design-pr4-validation.md) |
| Telemetry T01–T05 | 5 | [PR 5](design-pr5-validation.md) |
| Prefix hashing F01–F05 | 5 | [Prefix](design-prefix-validation.md) |
| HTTP storage/admin H01–H14 | 14 | [PR 6](design-pr6-validation.md) |
| Final gates V01–V04 | 4 | This report and [performance](design-performance-results.md) |

## Historical evidence and decisions

`a08c279a` passed the pre-review 14 CI jobs and four statuses. Cleanup-only `300625ac` failed Linux diff coverage
because a cleanup-fixture test used a two-second outer wait. That failed run is preserved externally; the review
retains the deterministic cleanup assertions and uses a 30-second hang guard. Neither old result proves the new code. Source checkpoints here retain their original hashes; the external
source bundle and commit map preserve them across raw-artifact cleanup. That cleanup retains all 536 commits
and 56 merge commits; the source/test/workload/build trees are unchanged.
`6e894fb7` passed Linux/macOS CI but failed Windows because the new startup-validation tests used the default
Event Log provider without machine permissions. The tests now clear logging providers. The next `2534cf15` run
passed Windows/macOS but exposed a stale native HTTP admin expectation on Linux; it remains a failed run.
The source of incidental allocations observed in the earlier macOS/Linux probe failures remains unconfirmed;
passing subsequent probes is a regression check, not a runtime root-cause diagnosis.

Johan approved combined review against main, replacing separate acceptance of historical PR versions, and five
exact HTTP time caps on 2026-10-17. Historical overages and failed/noisy studies remain unchanged. The existing
six cost exceptions, two sync/int SipHash caps and 6% interval-width requirement remain in force.

Raw profiles, measurement launches, CI logs, manifests and the original source bundle stay in the
[external archive](design-performance-results.md#external-measurement-archive). Only authored summaries,
reproducible tooling, approved rules and intentional compatibility fixtures belong in Git.
PR #134 and ASCII-hash PR #138 have been merged. No package release or PublicAPI promotion is included in this follow-up.
