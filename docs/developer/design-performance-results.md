# Design performance results and external evidence

The pre-review studies below remain historical. Fresh review-correction source `2534cf15` (native/cost
checkpoint `0949cb8a`) passes all 67 cost cases against f43854d, with zero failing cases and the same six
accepted costs. Redis and SQL native checks pass 11 and ten scenarios respectively. Receipts and provenance
are in the external archive’s `review/nimbus-0949/`; original source hashes remain resolvable from its bundles.
The fresh timing gate is open: Task/GUID hits measure 47.1 → 51.9 ns (1.098, 95% interval 1.075–1.121).
Sync/GUID and generic-hit studies are too noisy and remain failed. The first four-warmup study included
large compilation phase transitions; it is preserved unchanged. The harness now uses adaptive warmup, retains
warmup/actual data and rejects large actual phase changes. Diagnostic 100–140-warmup comparisons against
the pre-review source show the comment corrections improving Sync/Task hits by 3.4%/5.3% (five complete pairs);
those diagnostics are attribution evidence, not final acceptance against f43854d. No new time cost is waived.
The accepted numerical limits and 6% interval-width requirement are unchanged.


The design series is reviewed together in PR #134 against main. On 2026-10-17 Johan
approved five exact HTTP time limits in
[accepted.json](../../benchmarks/MethodCache.CostProfile/accepted.json), retaining
shared storage, metadata, administration and telemetry. Existing correctness,
byte/command and 6% confidence-interval precision requirements are unchanged.

| Scenario | Measured head/base | Approved cap |
|---|---:|---:|
| Local HTTP workload miss | 1.051 | 1.06 |
| Listening 304, before invalidation | 1.041 | 1.06 |
| Listening 304, after invalidation | 1.067 | 1.08 |
| Cold miss after invalidation, listeners off | 1.169 | 1.20 |
| Cold miss after invalidation, listeners on | 1.173 | 1.20 |

The case-scoped CPU review covers all 53 existing local cases and six affected
prefix misses: 59 complete cases, 42 faster, 12 same, five accepted, zero unaccepted
slowdowns, zero insufficient intervals and zero missing cases. Each selected whole
case retains every pair and actual iteration. Twelve Mac HTTP cases and two complete
Nimbus precision confirmations cover the fourteen changed HTTP cases; studies and
individual pairs are not pooled. The original entire Mac14 report still fails its
two wide intervals and is retained unchanged in the external archive.

The cumulative cost profile against `f43854dbcd3646893f490e78a0a1d9651e9c4521`
passes all 67 cases, with zero execution errors/failing costs and six existing
accepted costs. The five new time rules waive no bytes or commands. Separate F05
prefix and filtered SipHash CPU evidence retain their actual source points and the
two earlier exact sync/int caps. Historical failures remain failures.

On 2026-10-18 Johan approved three exact generated-contract time limits in
[accepted.json](../../benchmarks/MethodCache.CostProfile/accepted.json). They were
measured on a Ryzen 7 5800X (Windows 11) with 20 alternating pairs against f43854d and
waive no bytes, commands or precision. Raw launches are kept outside Git in
`/Users/johan/methodcache-evidence/` (`final-baloo/` and the `followup*-baloo/` folders).

| Scenario | Measured head/base | Approved cap |
|---|---:|---:|
| Record hit, non-ASCII key hashed again as UTF-16 | 1.143 | 1.18 |
| Fallback hit, JSON key for an object argument | 1.093 | 1.12 |
| Value-task miss | 1.048 | 1.08 |

## External measurement archive

Raw measurements, generated reports, per-launch logs, binary/source catalogues and
CI logs are artifacts, **never repository content**. Reproducible benchmark source,
the gate scripts, approved limits and this short summary remain in Git.

The original archive is kept outside every checkout at:
`/Users/johan/dev/MethodCache-artifacts/design-2026-10-17-a08c279a/`.
Its `design-benchmarks/` directory contains the original named study folders referred
to by older validation notes. `archive-manifest.json` lists 18,645 files,
146,602,636 bytes, and their SHA-256 digests. Manifest SHA-256:
`36f1263c7875f9d8a381759be79fa380dc6add3992593c4a3906310d97f26c15`.
`before-cleanup.bundle` preserves the original Git source points for independent
verification. These artifacts are kept separately and are not added to Git or LFS.
The small study summaries below describe the original measurement checkpoint;
cleanup changes no runtime, workload, numerical limit or measured result.

The final pre-cleanup runtime checkpoint is
`a08c279acf5f2a00859bfd08e64797ac7c8e1170`. It passes all fourteen CI jobs/four
statuses on Linux/macOS/Windows: 2,639 Linux tests, zero failures, one intentional
opt-in soak skip, 94.2% changed-line coverage, 99 randomized properties, enabled soak,
both cultures, required Redis/SQL, demo, AOT175, all seven packages/add-on smoke and
generated docs. The cleanup publication receives its own exact-head CI status.

## Reproduce

Use the benchmark code under `benchmarks/`, especially
[the cost profile](../../benchmarks/MethodCache.CostProfile/README.md).
Write results under ignored `artifacts/` or to a directory outside the checkout.
For an archived review, use the original study's scripts and rules in the external
archive; original SHA references are resolvable from its Git bundle. The current
rule file differs from its archived version only in its artifact documentation.
