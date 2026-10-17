# Design verification status

The complete design series is reviewed together in [PR #134](https://github.com/eoniclabs/MethodCache/pull/134)
against main, retaining source commit history. No raw measurement files remain in the PR history.

The independent review against main `8402ad2f` is complete. All 27 numbered review findings and three inline
findings have code/test corrections; [the review record](design-review-fixes.md) lists them and the additional
admin-id collision found during self-review. Review happened before any new push or full CI run.

## Current verification

Runtime source after the complete identity audit: `2534cf15da4eac6c8495ccd7980ada8c87dec43c`.
Frozen-fixture/native-consumer checkpoint: `6e894fb78db22069c6c3f93766922c04c5ff8679`.
`Contract1Final` explicitly freezes 104 Generated declarations and three vendor images from `3e82c06e`;
older unpublished checkpoints remain historical. Ordinary builds never regenerate them.

Local checks pass: solution build, 1,090 Core tests (one separately opt-in soak test), 544 generator integration
tests, 370 HTTP tests, 120 docs tests, seven binary/API compatibility tests and Native AOT on osx-arm64.
The complete benchmark test suite passes after charging only the exact longer-key string-size increment to
miss budgets; no closure allowance was widened. The preceding `6e894fb7` checkpoint passed 11 Redis and ten SQL native checks, with no infrastructure skips,
and the full 67-case cost profile (six existing accepted costs). Those results are kept separate from the later
identity corrections. Fresh `0949cb8a` native checks pass (Redis 11, SQL ten), and the full cost profile passes all 67 cases with
six unchanged accepted costs. The corrected functional checkpoint `f69719b4` passes all 14 CI jobs/four statuses on Linux, Windows and macOS:
2,734 Linux tests pass, zero fail, one opt-in soak case is skipped in the normal run; the enabled soak and 99
randomized properties pass separately. Changed-line coverage is 10603/11341 (93.5%). Required native providers,
both cultures, demo, Native AOT (175 corpus cases plus frozen vendor), all packages and generated docs pass.
Fresh paired CPU timing and exact post-cleanup publication CI remain pending. Historical source/CI hashes
are preserved externally; artifact-only history cleanup does not alter the verified runtime/test/workload trees.

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
Merge, release, PublicAPI promotion and integration of ASCII-hash PR #138 are separate actions.
