# Final design verification: functional checkpoint

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
