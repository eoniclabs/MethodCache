# GUID generated-workload origin preflight

Result equality alone allowed an uncached service to pass the old setup checks.
The new negative test first fails because no exception is thrown for an explicitly
uncached service. The corrected guard counts origin invocations outside timed hit
paths: eight initial populations, no additional origins over 100 repetitions of
each hit shape, and exactly one origin per distinct-key sync/Task/ValueTask miss.
A separate repeated-GUID mutation fails the miss guard. Both original sources
are restored before green verification.

The common workload registers the origin concretely and resolves the cached
interface through the same singleton. An origin-only Interlocked counter adds
work to factories, never resident hits; every compared revision uses these exact
sources, and earlier measurements cannot be pooled with them. Tests disable the
macOS performance-QoS setting; real benchmarks retain its existing default.

All twelve comparison points compile the identical three workload files and
Program. Every point passes `--verify-generated`, proving actual resident hit
behavior and thirty distinct-key misses. All compiled inventories match: 47 local
cases in five report families and 16 native cases in two. This is local setup
verification, not native backend setup, final Nimbus compilation or timing.

All 99 benchmark tests and all 40 Python validation tests pass locally. The
separate hash lookup now includes both Int and canonical GUID suffixes; its
four-runtime setup checks are separate evidence. Prior 64-case inventories
belong to the earlier source, before the redundant GuidHit was removed.

Per-runtime manifests retain exact runtime heads and common Program/workload
digests. The workload edits were compiled before commit, so the raw source
snapshots and digests identify the actual compiled bytes alongside the base SHA.
The final freeze must rebuild matching sources on Nimbus and preserve backend,
GC, SDK, CPU and independent-run requirements from section 10.
