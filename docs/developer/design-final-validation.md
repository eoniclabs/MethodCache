# Final design verification

## Current supported candidate: .NET 10

Johan approved .NET 10 as the only application runtime on 2026-10-15. The published stack is
PRs #123–#127, prefix hashing #128, HTTP storage #129 and the minimum-runtime change #130; this
final PR records acceptance evidence without changing the generated contract or frozen vendor files.

Runtime source `28ab9b22e02b0050a449c2bb1cfd977ec187ae25` passes the full required native-backend
release pipeline: **2,475 tests, zero failures, one intentional opt-in soak skip across 16 projects**.
The enabled soak, both cultures, 90 randomized properties, demo, native HTTP/AOT smoke, all 172
native signature cases and all seven installed packages pass. Independent Redis/SQL processes and
hash seeds, exact matching package dependencies and rejected mismatches pass.

HTTP diff coverage is **95.7% (733/766)** and cumulative design coverage is **93.9% (3,451/3,675)**.
The .NET-10-only PR adds no executable C# lines. Full receipt and environment details are in
[.NET 10 verification](design-net10-validation.md), with [selected logs](../../plans/design-benchmarks/net10-functional-28ab9b2/).
Earlier .NET 8/9 results below describe their historical revisions, not additional supported targets.

After that complete pipeline, two allocation probes were moved into synchronous NoInlining helpers,
outside assertion/async test scaffolding, with unchanged zero-byte assertions and explicit completed-hit
requirements. The four relevant facts pass again on .NET 10. No shipped source or benchmark workload
differs from the frozen 28ab9b2 candidate. Linux CI must confirm this probe change.

The preceding HTTP-only CI run at 8ffa85c passed Windows/macOS but failed Linux in those probes,
and one net9 Redis contract setup failed in subscriber Ping with TaskCanceledException. The latter
remains an unexplained historical startup observation; no production fix is claimed. Selected failing
and updated-probe output is preserved alongside this final receipt.

## Remaining acceptance gates

- Section 10: all adjacent behavior changes and the cumulative f43854d comparison need three
  independent alternating launches, reviewed 95% intervals and no hit allocation increase. Wide
  intervals are insufficient evidence. There is no accepted full-series timing result yet.
- Prefix component/facade measurements and current HTTP publication results must pass the same review.
- Every published head needs its required three-platform CI; drafts remain unmerged meanwhile.

The approved one-off Nimbus series uses the existing pinned .NET 10 image, frozen identical workloads,
exclusive nonblocking capacity leases of at most 25 minutes per batch and exact task-owned cleanup.
CI runs between batches; no external jobs are interrupted. The updated final candidate replaces only
8063e9a in a separate evidence cohort. Frozen successful measurements for unchanged revisions can
be reused with their original manifests; rejected/superseded runs are retained. Focused comparisons
check generated hits and HTTP miss/304 publication before resuming the complete matrix.

Dropping runtime targets simplifies build/test support; it is not a throughput claim. A supplementary
raw-IL diagnostic compared the seven net10 library outputs before/after that change: all 3,702
non-version-initializer method bodies/signatures/locals/exception regions match. The seven differences
are generated ThisAssembly commit-date initializers. This diagnostic is not a substitute for section 10.

---

# Historical functional checkpoints

Runtime source: `6c9881e0c89d3fe3c4e507879411d3e7493a92a6`, based on HTTP checkpoint
`ee1920db6cb5` and prefix candidate `850cef8f3bc709eaf464b3c25e5ec898217fcc73`.
This receipt is functional evidence. Section-10 timing and publication/external CI are still required.

The complete release pipeline finished successfully with required real Redis and SQL Server backends in fresh
containers, on macOS ARM64, SDK 10.0.103. No behavior source was edited during the pipeline.

| Framework | Passing tests | Failed | Intentional soak skips | Project runs |
|---|---:|---:|---:|---:|
| .NET 10 | 2,457 | 0 | 1 | 16 |
| .NET 8 | 2,333 | 0 | 1 | 15 |
| .NET 9 | 2,338 | 0 | 1 | 15 |
| Total | 7,128 | 0 | 3 | 46 |

Changed executable source coverage since `ee1920d`: **212/222 = 95.5%**, exceeding the 80% gate. Both cultures,
random-seeded properties on net10/net8, enabled soak, demo, Native AOT smoke (now including private HTTP variants,
HTTP-only administration and source-generated metadata JSON), all 172 native signature cases and all seven packed
packages passed. Package smoke includes actual .NET 8 SDK checks and independent Redis/SQL CLR/hash-seed phases.
The known KeyArguments/SharedKeyCollectionState AOT warning categories remain; this is not a zero-warning claim.
Frozen contract-1 vendor sources are unchanged from PR 1.

[Selected pipeline receipts](../../plans/design-benchmarks/final-functional-6c9881e/) retain the driver, changed-line
coverage, native smoke/corpus and installed-package output. Full local logs use `/tmp/methodcache-release-final-*`.

The final audit also corrects contradictory legacy documentation about custom backplanes, AnyKey, duration grammar,
runtime disabling, module selection and the removed OpenTelemetry wrapper. Those prose changes regenerate snippets
and LLM documentation and run the documentation examples again.

## Benchmark setup correction

The historical HTTP storage needed `HttpCacheStorageOptions.UseDistributedCache = true`; selecting a method-cache
backend alone did not enable HTTP L2. Earlier `--verify-native` origin counts proved caching, but were insufficient
to prove that older HTTP configurations actually used L2. They are not accepted as historical native-path evidence.
No accepted section-10 timing used those configurations. Existing local HTTP diagnostics are unaffected.

A new setup guard first failed on `f43854d` with “HTTP benchmark selected local storage for backend Redis”
(`/tmp/methodcache-http-legacy-guard-red.log`). Setup now enables the old option explicitly via reflection and assigns
the same isolated prefix; current revisions retain their automatic Core-owned selection. The guard verifies the
actual storage mode before measuring, and fresh-hit/miss origin counts still prove the chosen path. All eight native
setup checks then passed on the historical base (`/tmp/methodcache-http-legacy-guard-green.log`). This reflection is
setup-only and is absent from measured code. Preserved-cookie bypass is classified as a separate workload where the
earlier API did not offer KeepCookies.

## Rejected initial broad timing attempt

The first 62-case final launch and the interrupted prefix launch at `5fa2d16` are **not acceptance evidence**.
The initial activity monitor recognized .NET build/test processes but missed concurrent external Flutter tests.
Four Flutter workers were observed at roughly 79%, 78%, 76% and 26% CPU while the prefix launch was active;
individual sample times also drifted substantially. This observation does not identify the cause of every earlier
sample. The complete attempt is rejected rather than treating an absence of .NET activity as quiet-machine proof.

Only this task's BDN host was interrupted; its native containers were disposed, and external tasks were untouched.
[Raw reports, logs and rejection record](../../plans/design-benchmarks/series-rejected-dotnet-only-5fa2d16/) are retained.
The new Flutter-worker regression test failed before the guard fix. Five guard tests now pass, covering external
workers, own benchmark descendants, unrelated backend/apps and multiline process arguments. Local timing is also
separated from native backend startup. The entire acceptance matrix must be repeated with the corrected guard.

## Final exported API audit

The replaced fluent root/service/group interfaces had no public entry point after startup moved to
`MethodCacheBuilder`, but remained exported. Three new API-shape cases failed before internalizing those types and
passed after the change. Public method configuration results remain available. Public API records and XML references
are updated; the generated contract-1 surface and frozen vendors are unaffected. The complete release pipeline is
rerun for this source change before restarting frozen timing.

At `a711ad3ae39e8a8d295336418098c799eb2a75c6`, the complete rerun passed: **7,143 tests, 0 failures, 3 intentional
soak skips across 46 project/framework runs** (net10: 2,462; net8: 2,338; net9: 2,343). Cultures, random properties on
net10/net8, enabled soak, demo, native HTTP/AOT smoke, the 172-case native corpus, all seven packages, actual SDK8
and independent Redis/SQL processes passed again. These visibility/XML changes add no executable source lines;
the earlier runtime changed-line coverage remains 95.5%. [Selected current receipts](../../plans/design-benchmarks/final-functional-a711ad3/)
retain the driver, coverage decision, native and installed-package output. Full local logs use
`/tmp/methodcache-release-coordinator-*`. The new guard's five Python regression tests also pass. Performance and
external CI remain required; the earlier broad timing attempt remains rejected.

The process guard was then refined for four nine-day-old sleeping orphan Flutter workers observed at 0% CPU.
They must not block quiet timing forever, and are left untouched. State/age/CPU-time sampling distinguishes them
from new or CPU-active workers, including CPU progress whose lifetime percentage rounds to zero. The same collector
regression fails against the earlier committed implementation and passes after the fix; all eight Python guard
tests pass. These are measurement-tool changes and do not alter the library source verified at `a711ad3`.
