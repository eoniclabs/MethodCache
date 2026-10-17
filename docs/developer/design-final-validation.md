# Design verification status

The completion claim below is historical and superseded by the 2026-10-17 PR review.
Review fixes change runtime behavior and the unreleased generated contract. Final acceptance,
performance and full CI must be repeated against the resulting source. Johan requires a complete
independent self-review of the entire PR before another CI run is started. Targeted local tests
remain allowed; no new CI has been started for these changes.


The complete design series is reviewed in [PR #134](https://github.com/eoniclabs/MethodCache/pull/134)
against main. Source history is retained with raw artifact paths removed. Implementation,
benchmark workloads, generated ABI and the approved numerical limits are unchanged by
artifact cleanup. Exact cleanup-head CI is the authoritative final publication gate.

## Functional verification

The pre-cleanup final checkpoint `a08c279acf5f2a00859bfd08e64797ac7c8e1170` passes
all 14 CI jobs and all four statuses on Linux, macOS and Windows: 2,639 Linux tests,
zero failures, one intentional opt-in soak skip, and 3615/3839 (94.2%) changed-line
coverage. Separately enabled soak, 99 randomized properties, both cultures, required
Redis/SQL, demo, Native AOT with 175 signature cases, all seven packages/add-on smoke
and generated docs pass. All twenty frozen vendor/source/manifest files and all 103
generated contract declarations are preserved.

The 76 explicit acceptance requirements in [the specification](design-spec.md) are
mapped to inspected assertions and exact original source/report hashes in the
external archive's `final-acceptance-inventory-v1/acceptance.json`. The final audit
verifies that every required scenario is present, tests the current code in full CI,
and separately checks the performance gate. The per-step validation notes remain:

| Requirement group | Count | Validation |
|---|---:|---|
| Generated contract and binding G01–G14 | 14 | [PR 1](design-pr1-validation.md) |
| User API and policy P01–P16 | 16 | [PR 2](design-pr2-validation.md) |
| Keys and diagnostics K01–K08 | 8 | [PR 3](design-pr3-validation.md) |
| Transport and format D01–D10 | 10 | [PR 4](design-pr4-validation.md) |
| Telemetry T01–T05 | 5 | [PR 5](design-pr5-validation.md) |
| Prefix hashing F01–F05 | 5 | [Prefix](design-prefix-validation.md) |
| HTTP storage/admin H01–H14 | 14 | [PR 6](design-pr6-validation.md) |
| Final gates V01–V04 | 4 | This report and the performance summary |

## Performance and decisions

Johan approved combined review against main with history preserved, replacing
separate cost acceptance of historical PR heads. Their seventeen measured cost
failures remain historical failures. He subsequently approved alternative 1 on
2026-10-17: five exact HTTP time caps, with reasons, in canonical accepted.json.
All other correctness, cost and precision rules remain unchanged.

[Performance results and archive inventory](design-performance-results.md) describe
the accepted 59-case CPU coverage and 67-case cost profile. Original failed/noisy
studies are retained externally and are never relabelled or pooled. Earlier precise
unaffected cases keep their actual source points and explicit source-impact review;
L2 commands remain the L2 gate and new work is reported separately.

## Artifact policy

Raw measurements, generated reports, catalogues and full CI logs live outside Git.
The complete original evidence and its source-point Git bundle are preserved in the
[external archive](design-performance-results.md#external-measurement-archive).
Repository tests guard against accidentally tracking measurement outputs again.
The artifact cleanup changes storage/documentation and this guard only; the final
runtime/workloads and all numerical accepted limits remain unchanged.

Merge, package publication, PublicAPI promotion and the separate ASCII-hash PR #138
remain outside this implementation scope.
