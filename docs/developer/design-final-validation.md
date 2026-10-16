# Final design verification

## Latest complete three-platform functional checkpoint

Combined head `dd6b1c6a7b7ca47396d2ac4a06fd0df4332a45b0` passes all fourteen CI jobs
and all four commit statuses. Linux runs 2,561 facts across sixteen projects, with no failures
and the single intentional opt-in soak skip. Required Redis/SQL, randomized properties,
enabled soak, both cultures, demo, AOT175, all installed packages and docs pass; changed-line
coverage is 3486/3708 (94.0%). The complete no-listener probe passes on all three platforms.
The preceding 6caa856 also passes all fourteen jobs. Neither result explains the historical
272-byte object's exact source or establishes timing acceptance. Subsequent analysis-tool and
documentation edits require their own published-head CI.
[Exact logs, exits, source hashes and commit statuses](../../plans/design-benchmarks/combined-functional-dd6b1c6/).

Source review before full timing also corrects the archived V4 analysis helper's L2 label:
enabled round-trip validation already existed before PR4. The new opt-out changes writes;
all measured L2 reads remain comparable. Use the corrected reviewer under benchmark validation,
retaining the original archived helper and its failed classification as historical evidence.
Six assertion subtest failures reproduce the error before correction; all twelve summary tests
and all 52 benchmark-validation Python tests pass afterward. The deployed measurement protocol,
workloads, process order and sample counts are unchanged. This correction grants no timing waiver.
[Fourteen exact source checks, statistics defaults and red/green evidence](../../plans/design-benchmarks/guid-feature-review-v1/).

## Whole-probe allocation warmup

The 388050f Linux jobs and Windows build/unit checks pass, but macOS unit CI reports 272 bytes in
the strict no-listener telemetry allocation fact. Its exact allocation source remains unproven. The
APIs had a warmup loop, but the complete NoInlining probe, including its allocation-counter calls and
loop/JIT path, was invoked for the first time inside the measured window. The fixture now warms that
whole probe once, then measures one fixed 10000-pair window with the same exact-zero assertion.
It does not retry, select a minimum or permit small allocations.

A new negative guard injects one escaping object only inside that final window and detects it.
Separate escaping-array mutations in each real telemetry API fail the unchanged zero assertion;
production source is restored byte-for-byte. All 1039 Core facts and the full fourteen-project unit
job (2465 facts, one intentional opt-in soak skip) pass locally. This improves the warmup boundary,
without claiming the original 272-byte object's type or replacing fresh external CI.
[Original failure, single-allocation guard and both API mutations](../../plans/design-benchmarks/telemetry-whole-probe-warmup/).

## CI startup, source-file and timeout follow-up

The exact 8647bdc CI run fails compilation on all three platforms: the genuine long-prefix benchmark
produces a decorator hint longer than a filesystem filename component. Three new corpus cases cover
long ASCII namespaces, Unicode namespaces and interface names, including distinct interfaces with the
same leading text. Their portable file-emission tests fail before bounded deterministic hint names are
introduced. Generated types, method IDs, key text and the frozen contract are unchanged.

The Redis subscriber-Ping failures are explained by the pub/sub connection still establishing after
the interactive connection becomes ready under FailFast. The [PR131 investigation](https://github.com/eoniclabs/MethodCache/pull/131#issuecomment-6202008541)
reports its reproduction. Test setup now retries only connection/cancellation failures within a bounded
20-second deadline; deterministic FakeTimeProvider cases cover first-ping failure, success, hanging pings,
deadline expiry and unrelated exceptions. A later full-suite coverage run also exposes the policy-state
sync scenario beginning before its native subscription is acknowledged. That fixture now awaits its
existing message gate before cache assertions; original value/factory assertions remain unchanged.

The old B925 coordinator SSH call timed out after 3600 seconds, but the remote Docker client/testhost
and run-owned containers survived for over eight hours. The next coordinator enforces the timeout on
Nimbus itself and keeps SSH open through cleanup grace. A controlled actual-Nimbus fixture confirms
that server timeout invokes exact-label cleanup without making real Docker calls. Nimbus uses uutils
timeout and reports exit 125 in that fixture; no success or specific 124 code is assumed.

The independent Git clone race has a [reproduced fix in PR136](https://github.com/eoniclabs/MethodCache/pull/136):
automatic maintenance must finish before the mirror is cloned. Coordinator changes require installation
with `--update-only`; source changes alone do not update the installed scheduler. No running job is
stopped by publishing or atomically installing that fix. On 2026-10-16 Johan explicitly authorized the
old SDK cleanup. Only its verified container was stopped; Redis/SQL states and start timestamps match
before and after. The old diffcoverage result is exit 137, and its orphan shell/testhost released the
capacity lock. [Exact scoped cleanup evidence](../../plans/design-benchmarks/approved-orphan-sdk-stop/).

Local follow-up verification passes all 2,560 facts across sixteen projects, with required native Redis/SQL
and the single intentional opt-in soak skip. Changed-line coverage is 3486/3708 (94.0%); native macOS
AOT smoke and all 175 signature cases pass. After integrating PR136, 48 CI Python cases pass and two
publishing checks skip because they require a newer local SDK. Original failures, pre-fix regression
outputs and exact proof scopes are retained in the [CI follow-up receipt](../../plans/design-benchmarks/ci-startup-followup/).
These local passes do not establish the next published head's three-platform CI result.

## Native fault-fixture failure and bounded cleanup

The b92553d Linux diff-coverage run stalled in the Redis policy-fault fixture. A private diagnostic
snapshot shows an Empty assertion failure unwinding through shutdown while its own test write remains
behind an unreleased gate. The fixture now counts held operations separately from earlier receipts and
always releases that gate in finally. Two regressions fail before the fix; all 1038 Core facts and the
original three shared cases on each real Redis/SQL provider pass afterward. Shipped source is unchanged;
the original CI run remains failed/stuck evidence, never a substituted pass.
[Exact safe diagnostics and red/green receipts](../../plans/design-benchmarks/native-fault-fixture-cleanup/).

## Exact comparison bases and generated prefix coverage

The constructor and SipHash branches use an earlier HTTP implementation than the new combined candidate.
All ten PR merge bases were checked against current GitHub base/head snapshots and `git merge-base`.
Constructor PR132 must compare **9767fec → 2b3cb93**; isolated SipHash PR133 compares **2b3cb93 → 4f6cd73**.
The full preparation therefore includes fourteen runtime points. Main GUID workloads remain identical,
and the cumulative comparison remains f43854d → the final combined runtime. Neighboring points in a
flattened list are not automatically valid isolated PR comparisons.

The earlier prefix benchmark calls the Generated facade by hand. F05 additionally requires real generated
decorators with short/long prefixes and both listener states. `GeneratedPrefixHitPathBenchmarks` now calls
actual service interfaces, including a real long namespace, sync/Task/ValueTask hits and unique misses.
GUIDs are the primary workload; separately labelled int controls retain the known regression check.
All six component runtime points compile and pass setup guards with the same four C# files and program.
Their actual inventories agree on **102 cases: 18 lookup, 36 facade/reference and 48 generated-interface**.
Split these three families into separate bounded leases; do not drop cases to fit one oversized batch.
Local setup/inventory proof and constant-key mutation sensitivity do not imply performance acceptance.
[Exact six-runtime source/program/inventory proof](../../plans/design-benchmarks/generated-prefix-f05-preflight/).

The prior exact GUID checkpoint c88f145 passes every required functional CI job: 2,528 main-suite facts
across sixteen projects, 91 randomized properties, enabled soak, both cultures, native Redis/SQL,
172 AOT signatures, seven installed packages/add-ons and docs. All fourteen exits and four commit
statuses are success; changed-line coverage is 3468/3690 (94.0%). It precedes the HTTP header-copy
candidate and the new generated-prefix workload, so it is not latest-head or timing acceptance.
[Exact C88 jobs, counts, hashes and statuses](../../plans/design-benchmarks/guid-functional-c88f145/).

## Completed HTTP response-reuse confirmation

The fixed three-pair Nimbus comparison of prefix 850cef8 with response-reuse 12bf99d
is complete. The listener-on/post-invalidation case regresses **9.36%**, with disjoint
95% intervals over the three independent launch means. The other three cases have
insufficient precision. All six launches completed with twelve actual iterations per
case and empty interference guards. Allocation decreases do not waive this failed gate.
[Exact results, frozen inputs, complete logs and rederived comparison](../../plans/design-benchmarks/http-304-response-reuse-confirmation/).

A new candidate merges 304 headers directly into the replacement dictionary and avoids
per-response filter allocations when Connection options are absent. The local header-only
probe falls from 1248 B to 880 B; this is not full HTTP timing evidence. All 322 HTTP tests
pass, including two new preservation cases that also pass before the optimization, and
all nine changed executable lines are covered. Its completed six-launch Nimbus comparison
has insufficient precision in all four cases: mean changes are +2.17%, +0.88%, +8.03% and
+7.16%, with overlapping/wide intervals. Allocation falls from 8256 B to 7168 B without
listeners and 7368 B with listeners; this does not establish timing acceptance.
[Exact three-pair header-copy results](../../plans/design-benchmarks/http-304-header-confirmation/).

A separate [fixed nine-pair protocol](../../plans/design-benchmarks/http-304-header-affinity-protocol-v1/)
starts both revisions on CPU2 before CLR startup and verifies actual affinity for the host
and four benchmark children. All eighteen launches and four cases are retained; previous
samples are not pooled and there is no early stopping. It reuses the same compiled inputs,
pinning all 138 DLLs per side. Results remain pending; section 10 stays open.

The older int-baseline startup-affinity study also completes all nine independent launches.
Its SyncHit/TaskHit intervals narrow, but GenericHit/ValueTaskHit remain broad. It has no
change comparator and cannot prove an optimization, GUID precision or HTTP performance.
[All nine baseline runs, actual affinity and intervals](../../plans/design-benchmarks/startup-affinity-baseline-v4/).

## Current supported candidate: .NET 10

Johan approved .NET 10 as the only application runtime on 2026-10-15. The published stack is
PRs #123–#127, prefix hashing #128, HTTP storage #129 and the minimum-runtime change #130; this
final PR records acceptance evidence without changing the generated contract or frozen vendor files.
Its CI lease fix also covers remote checkout/collection before builds, so those operations cannot
overlap an exclusive benchmark batch.

The complete current standalone 9767fec checkpoint passes all ten Linux jobs and
macOS/Windows build/unit jobs: **2,519 tests in 16 projects, zero failures and one
intentional opt-in soak skip**, including the current epoch, telemetry and strict
body-budget probes. Required native backends, 90 randomized properties, enabled soak,
cultures, 172 AOT signatures, installed packages and docs pass. All three platform and
aggregate statuses are success. Its shipped sources exactly match runtime 12bf99d;
constructor/hash candidates and section-10 timing are excluded.
[Exact current standalone head, exits and logs](../../plans/design-benchmarks/net10-functional-9767fec/).
Earlier [e84a549](../../plans/design-benchmarks/net10-functional-e84a549/) and
[b518139](../../plans/design-benchmarks/net10-functional-b518139/) receipts remain historical.
The earlier .NET-10-only #130 head befb50a also passes all three platform and aggregate
statuses. The telemetry/null-key fixture follow-ups f793d0a and 1b43211 are historical.
Current #129 f0dd6c2 includes the 304 header-copy optimization; current #130 1bafff1 merges it.
Both require fresh CI; the old 0dae335 macOS net9 failure is retained below.

The pre-optimization runtime source `28ab9b22e02b0050a449c2bb1cfd977ec187ae25` passed the full required native-backend
release pipeline: **2,475 tests, zero failures, one intentional opt-in soak skip across 16 projects**.
The enabled soak, both cultures, 90 randomized properties, demo, native HTTP/AOT smoke, all 172
native signature cases and all seven installed packages pass. Independent Redis/SQL processes and
hash seeds, exact matching package dependencies and rejected mismatches pass.

HTTP diff coverage is **95.7% (733/766)** and cumulative design coverage is **93.9% (3,451/3,675)**.
The .NET-10-only PR adds no executable C# lines. Full receipt and environment details are in
[.NET 10 verification](design-net10-validation.md), with [selected logs](../../plans/design-benchmarks/net10-functional-28ab9b2/).
Earlier .NET 8/9 results below describe their historical revisions, not additional supported targets.

The newer HTTP-optimized standalone #130 source `12bf99d877c33925fe0d0037f45e652d0260d8a4`
subsequently passed every Linux functional job: 2,476 tests in 16 projects, zero failures,
one intentional soak skip; required native backends, 90 randomized properties, enabled soak,
cultures, demo, 172 AOT signatures and all installed-package checks. Windows/macOS build and unit
jobs pass. Cumulative coverage is 3,462/3,686 (93.9%). Its overall CI nevertheless failed because
`llms-full.txt` lacked the three generated HTTP changelog lines. That drift is repaired in the
stack; newer-head CI remains required. This receipt does not include #131's additional policy
fixtures or unfinished #132/hash changes. [Exact jobs, exits and failure](../../plans/design-benchmarks/net10-functional-12bf99d/).

After that complete pipeline, two allocation probes were moved into synchronous NoInlining helpers,
outside assertion/async test scaffolding, with unchanged zero-byte assertions and explicit completed-hit
requirements. The four relevant facts pass again on .NET 10. Later source changes are described below; the 28ab9b2 receipt is historical evidence for its exact revision. Benchmark workloads were
subsequently corrected as described below; their evidence must use the same corrected sources on every revision.

The preceding HTTP-only CI run at 8ffa85c passed Windows/macOS but failed Linux in those probes,
and one net9 Redis contract setup failed in subscriber Ping with TaskCanceledException. The latter is
explained by the pub/sub startup race and addressed in test readiness as described above; no production change is claimed. Selected failing
and updated-probe output is preserved alongside this final receipt.

## Remaining acceptance gates

- Constructor PR #132 at 2b3cb93 and SipHash PR #133 at 4f6cd73 pass current commit statuses.
  The GUID-workload update #135 at 419c786 is integrated in the combined branch. Its Linux
  and Windows jobs pass, but macOS fails before tests because the exact retained CI clone
  lacks reachable Git objects. Native Git confirms that failure; the mirror itself is healthy.
  PR136 reproduces the detached-maintenance race and supplies its fix. Preserve the [exact failure and
  connectivity checks](../../plans/design-benchmarks/guid-macos-history-419c786/).
- Primary workload bytes are frozen at B925 and component workload bytes at bbff4a6.
  Complete Nimbus preparation of all fourteen runtime points and six component points,
  including actual image-ID validation for the pinned Redis/SQL containers. Then validate
  inputs and run the fixed 180-launch matrix. The full dispatcher has not started. Compare
  identical key types and workloads across revisions; keep int and GUID results separate.
- Section 10: all adjacent behavior changes and the cumulative f43854d comparison need three
  independent alternating launches, reviewed 95% intervals and no hit allocation increase. Wide
  intervals are insufficient evidence. There is no accepted full-series timing result yet.
- Prefix component/facade measurements and current HTTP publication results must pass the same review.
- Every published head needs its required three-platform CI; drafts remain unmerged meanwhile.

Exact current-head functional receipts are now collected for [HTTP f0dd6c2](../../plans/design-benchmarks/http-functional-f0dd6c2/),
[.NET 10 1bafff1](../../plans/design-benchmarks/net10-functional-1bafff1/) and
[SipHash 4f6cd73](../../plans/design-benchmarks/siphash-functional-4f6cd73/): fourteen job exits and all four
commit statuses pass for each. HTTP's historical branch still ran three frameworks; that does not
expand the final .NET 10 support target. Combined-head CI and timing are separate remaining gates.

The [frozen version-4 protocol sources](../../plans/design-benchmarks/guid-full-protocol-v4/) retain all
180 planned launches and the actual PR comparison edges. Its newly deployed validation controller waits
for preparation, then checks all sixty compiled family inventories without creating a timing directory.
The full measurement dispatcher remains stopped until those inputs are reviewed.

An additional historical #129 package run at 2bb7c0 failed its net8.0 SQL independent-process
check at phase 2, exit 5: the L2 value and zero origin calls had passed, but the expected key
was absent from the following local admin list. Three fixed diagnostic SQL rounds on macOS/.NET 10
pass all 27 process phases with the original assertions and installed Core source; they do not
explain or repair the Linux observation. No production/assertion change is claimed. The supported
12bf99d net10 package job passes separately; latest-head CI remains required. Preserve this failure
as unresolved evidence: [original output and diagnostic provenance](../../plans/design-benchmarks/seed-isolation-admin-observation/).

The approved one-off Nimbus series uses the existing pinned .NET 10 image, frozen identical workloads,
exclusive nonblocking capacity leases of at most 25 minutes per batch and exact task-owned cleanup.
CI runs between batches; no external jobs are interrupted. The final runtime candidate replaces 8063e9a.
The corrected cold-publication workloads and complete CI capacity boundary require fourteen runtime
points, including the actual post-PR122 merge-base of PR1, the constructor's older standalone base,
the isolated SipHash head and the cumulative f43854d baseline. Earlier measurements remain separate
diagnostics; they are not pooled into this cohort. Focused comparisons check generated hits and
HTTP miss/304 behavior while the new identical harnesses are prepared.

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

## CI capacity boundary follow-up

The original coordinator acquired capacity inside linux.sh, after remote fetch/checkout and old-run
collection. A focused mocked remote-execution regression fails against that implementation: denying
capacity still allows checkout. The coordinator now acquires one shared descriptor before any of
those operations and holds it through the remote jobs. It clears the inner lock setting to avoid
nested reacquisition behind an exclusive waiter. A second behavior fact verifies that the descriptor
remains open during the mock build and the nested setting is absent. Active passes are not restarted.

This closes a verified isolation gap for future measurements; it does not prove that every earlier
sample experienced competing checkout work. Original evidence remains identified by its environment
and measured intervals. No library runtime or benchmark workload source changed.

## Bounded HTTP publication workload

The original HTTP miss case grows its cache across iterations. Older storage charges only body bytes
against its default 100 MiB limit; the new storage also charges headers and metadata. Consequently
that workload combines publication, different eviction pressure and a growing GC live set. It is
retained as a pressure diagnostic rather than being labelled a pure cold-publication comparison.

HttpColdMissBenchmarks uses a new isolated provider outside each measured iteration, with exactly
8,192 calls and unroll factor 1. Its invocation count overrides the normal 500 ms iteration target.
Four end-to-end facts run two complete batches each, prove every call loads origin, preserve all
8,195 resident representations below budget, and prove the next batch starts with only its three
warm representations. Removing reset makes all four fail. A fifth fact checks BenchmarkDotNet's
actual job configuration and reset hook. All 91 benchmark contracts pass on net10.0.

The shared validation harness now selects cold publication plus fresh/variant/304 paths separately;
there are still 62 scenarios, with one additional JSON report. All nine runtime revisions need the
identical new workload sources and fresh evidence; older growing-miss samples cannot be pooled into
this case. No shipped runtime source changed. Evidence: `plans/design-benchmarks/http-cold-publication/`.

## Exact identity allocation probe follow-up

HTTP head 953e04b passed macOS/Windows, but its Linux .NET 9 long-key identity probe reported
20,200 bytes against 16,000 expected. Final head f1da892 also failed that probe on macOS/.NET 10
(23,080 versus 16,000). These are retained failures, not dismissed as obsolete-runtime behavior.
The isolated local probe passed before this change, so neither a deterministic local reproduction
nor the origin of those historical extra allocations is claimed.

The synchronous probe runs on an owned thread without inherited ExecutionContext. Thread/delegate
creation, warmup, completion and assertions stay outside the NoInlining measurement helper. A later
Linux culture run of 771980a still counted 16,024 bytes in the output-only reference versus 16,000 in
Identity. Its retained failure shows that one-batch reference measurement was insufficiently stable;
the source of the 24 extra reference bytes is not established.

Both paths now use three fixed batches of 100 calls and compare their minima with exact equality and
no byte tolerance. This tests sustained per-call temporary copies, rather than claiming that every
individual invocation is free of transient runtime allocation. A temporary production mutation copying
the UTF-16 input into a new buffer before hashing makes all four inputs fail. Restored production
passes all 320 HTTP facts on each historical target and under tr-TR/sv-SE on net10. No shipped source
or measured C# workload changes; this local verification does not establish latest-head CI acceptance.

[Original failures and mutation/green receipts](../../plans/design-benchmarks/identity-probe-isolation/)
are preserved. The forwarded latest heads of #129, #130 and #131 still require fresh three-platform CI.
Earlier green statuses do not verify these newer heads.

## Native policy fault acceptance

The final acceptance audit found that P11/P12's exact partial-result and cancellation/recovery assertions
ran against the in-process fault store, while the existing native policy contract covered successful
confirmation only. `NativePolicyFaultContract` is now compiled unchanged into the model, Redis and SQL
test projects. It injects one stamp failure and one publication failure around the supplied provider;
all successful operations delegate to that actual provider. It checks exact 3/3/2/1/1/1 counters,
independent drained queues, cancellation after local mutation with owned completion, and validation/
deduplication/early cancellation. Confirmed timestamp bytes are read from the supplied backend.

A deliberately false stamp acknowledgement that skips the provider write fails all three real Redis
scenarios, demonstrating that wrapper counters cannot stand in for durable writes. The restored
contract passes all three scenarios on the model, Redis and SQL. No production or benchmark workload
source changes; current-head CI is still required. Selected receipts are under
`plans/design-benchmarks/native-policy-fault-acceptance/`.

## Native policy state acceptance

Nine additional scenarios run unchanged on the Core model and real Redis/SQL providers:

- Equal effective policy with reversed non-conflicting source and tag order, real L2 reuse and audit-only no-op.
- One-hour to one-second configuration reload, exclusion of an old-policy writer, and shared reuse after both reload.
- Explicit method Enabled values over group settings and higher-priority file defaults.
- Disabled caching during a held load or an expired-entry join of a held background refresh: eight distinct bypass
  calls, no value-write attempts by bypasses/retired work, cold shared-reader rejection and fresh re-enabling.
- Asynchronous recording/NoOp managers and synchronous custom-manager bypass.
- Native async cross-node sharing alongside the documented local/origin sync fallback.
- A held actual async backend read while sync calls complete, with zero synchronous backend reads.
- A connected native receiver that observes and drops a real method frame, retaining its own L1/policy until expiry.

The first reload test incorrectly assumed notification completion was also a native storage barrier. It failed
on both native providers while passing the model. The corrected test awaits PolicyRegistry's actual owned
invalidation task, asserts readiness and empty queues, then advances the fake clock beyond the stored stamp.
Diagnostic output confirms native readiness was false before that wait. This follows section 5.1; no production
fix or global barrier is claimed. An early unsubscribe-based loss fixture also failed on Redis; that observation
is preserved without assigning a root cause. The final delivery gate controls frame loss on an acknowledged
subscription and waits for the specific dropped method frame. SQL polling uses the injected clock.

A gate mutation that delivers rather than drops the frame makes the loss scenario fail (101 versus 201),
demonstrating that the scenario depends on real delivery control. The original deadline contract now uses the
specification's one-hour shared policy, one-second private override and +2 second fake-time expiry. All successful
store operations still reach the supplied provider. Selected receipts and current full-suite counts are in
`plans/design-benchmarks/native-policy-state-acceptance/`. No shipped runtime or measured workload source changes.

The native policy fixtures share a fake clock anchored ten minutes ahead of wall time. Physical
short-TTL rows therefore survive real setup delays, while explicit fake-time advances must expire
the source envelope. This separates deterministic logical-expiry assertions from database wall-clock
scheduling; server clocks and production behavior are unchanged.

## Deadline fixture subscription confirmation

The older net10-only head f0a492b passed Windows/macOS but failed Linux at the first manual deadline
L2 read (expected stored1, actual factory2, PolicyBackendContract line82). The failure remains recorded;
its root cause is not claimed. The timed section now owns two nodes and a fresh physical/backplane
prefix, and native setup awaits real subscription acknowledgement/initial callback completion before
returning a node. The previous policy scenarios and every deadline/sliding/fail-safe assertion remain.

A controlled subscription/callback fact fails if readiness completes without awaiting the underlying
operation. The restored barrier and native contracts pass on all three historical targets in the
earlier HTTP snapshot (9 selected facts). After forwarding into the supported final .NET 10 stack,
complete affected suites pass: **Core 1,034; Redis 27; SQL 63; zero failures, one intentional opt-in
soak skip**. Native infrastructure is required. This changes test prerequisites, not production code.
[Original failure, mutation and verification receipts](../../plans/design-benchmarks/policy-deadline-readiness/)
are preserved. Latest-head full CI remains required.

## Measurement precision review

The three focused prefix baseline launches are complete. Their generated-hit 95% intervals over run
means are broad: relative half-widths are about 14.6% sync, 39.7% generic, 7.9% Task and 24.5% ValueTask.
These are insufficient for section-10 acceptance, and do not by themselves prove a source regression.
The final comparison launch remains separate from the corrected cold-publication cohort.

A separate precision pilot tests fixed benchmark-process affinity (mask4, observed logical CPU2/core2)
with unchanged source/runtime/GC/JIT/features. No host governor, boost or service is changed. The first
four-case launch completed, but the verifier parsed BDN's binary affinity display in the wrong radix;
its original nonzero validation exit and complete diagnostic reports are retained. A fresh three-run
pilot uses corrected parsing. No first-run timing is pooled into that pilot or claimed as acceptance.
The component timing controller was stopped before preparation to await a sufficiently precise protocol.
Its later preparation-only controller failed during provider setup, before any timing. The standalone
harness selected BDN's transitive DI 6 implementation with DI 9 abstractions. This was reproduced
locally; explicitly selecting DI 9, as the benchmark tests do, makes all component setup checks pass
against both PR5 and the prefix runtime. The generated harness now includes those executable guards
and preserves a setup exception instead of masking it during cleanup. Native-host preparation and
actual timing remain required; see `plans/design-benchmarks/component-harness-startup/`.

## Local epoch allocation probe follow-up

An older Linux run of cf4caa6 reported 32,664 bytes instead of zero for the local epoch-vector probe.
The exact 10,000-capture measurement and 1,000 warmups now run on an owned thread without inherited
ExecutionContext. Store construction, completion and assertions remain outside the measurement; the
zero-byte requirement is unchanged. Replacing memoized identity reuse with a new string makes the
revised test fail with 1,520,000 bytes. Restored source passes the complete 320-case HTTP suite on
the historical three-target checkout. The original failure is retained without attributing its bytes
to a proven cause: `plans/design-benchmarks/local-epoch-probe-isolation/`.

The later #132 Linux/tr-TR run at 94ee6f0 still reports 680 bytes in that owned-thread single batch.
The follow-up fixture completes three fixed 10,000-capture batches after the same 1,000 warmups
and requires a zero-byte batch, without tolerance or adaptive retry. It checks sustained per-call
allocation, not every possible transient runtime allocation. All 30,000 captures also must return
the same identity object. A mutation copying only the first measured identity fails; an escaping
128-byte temporary buffer while returning the original identity fails with 1,520,000 bytes in each
batch. Production source is restored byte for byte. All 320 HTTP facts pass on net10.0 under the
default, tr-TR and sv-SE cultures. The original cause remains unproven, and whole-path performance
allocation gates plus latest-head CI remain required. Exact receipts are in that directory's
`fixed-batches/` subdirectory.

## No-listener telemetry probe follow-up

Historical net10 head 0626bdb passed Linux/Windows but its macOS unit job measured 2,256 bytes
in the no-listener telemetry test. That probe included an assertion inside the measured loop
and left BeginLoad unwarmed. The revised final-PR fixture warms both operations on an owned
thread, then measures the same 10,000 pairs in a synchronous NoInlining helper with assertions,
setup and completion outside it. The single batch still must allocate exactly zero bytes,
without tolerance or resampling; a non-null operation fails explicitly. The original byte
source remains unproven. Adding an escaping temporary buffer to either API fails with 1,520,000 bytes.

The complete Core suite passes 1,035 facts, zero failures and one intentional soak skip in the
default, tr-TR and sv-SE cultures. The formerly duplicated AppendNullPart theory row is now a
separate fact, so both null APIs receive distinct assertions and the duplicate-ID warning is
gone. Production source is unchanged. [Failure, sensitivity and final-suite receipts](../../plans/design-benchmarks/telemetry-allocation-probe/)
do not replace latest-head CI or the whole-path performance allocation gate.

## Independent-run precision follow-up

The first two CPU2-affinity baseline GenericHit means imply that even the best possible positive
third mean would leave a relative three-launch 95% interval half-width of at least 7.51%.
This originally motivated a nine-launch GenericHit supplement, but it was stopped before any timing
and replaced by the separate all-four-hit startup-affinity study described below. Its samples will
not be pooled with the earlier pilot. Neither the calculation nor the helper passes a performance gate.

The statistical report now supports complete fixed series of three, six or nine independent
launches, with the matching Student-t degrees of freedom. An explicit exact-method selector permits
targeted supplementary evidence without claiming extra launches for unselected cases. Missing
launches/guards/cases, differing launch counts and incomplete matrices are rejected. Default
three-launch behavior and the regression/allocation criteria remain unchanged. New collection tests
fail against the prior helper; all 25 statistical/activity guard tests pass. Evidence and the
[NIST table reference](https://www.itl.nist.gov/div898/handbook/eda/section3/eda3672.htm) are linked from
`plans/design-benchmarks/independent-run-precision/`.

## HTTP 304 timing stop and candidate optimization

The focused three-launch comparison of prefix runtime 850cef8 and pre-optimization final runtime
28ab9b2 is now complete. All four 304 revalidation cases meet the section-10 stop rule: mean time
increases 8.4–13.1% and the candidate interval lies wholly above the baseline interval. Allocation
falls from 8,256 to 8,016/8,216 bytes. Generated-hit intervals remain insufficient; the growing-miss
rows are pressure diagnostics. This is a failing timing check, not an overall acceptance result.

The new HTTP candidate removes duplicate foreground response/content/header construction. Its
caller-owned response is used for the same complete merged-header/storage/privacy validation before
adding caller-only headers; background validation owns its temporary response, and failures dispose
unreturned foreground responses. The historical HTTP checkout passes 320 HTTP facts, 85 benchmark
contracts and 13/13 executable changed-line coverage on net10. After forwarding, the final checkout passes all 320 HTTP facts, 91 benchmark contracts and
16 IHttpClientFactory/HTTP integration facts. The last suite uses in-process handlers, not native backends. New timing and complete latest-head CI remain required.
The new frozen .NET 10 source is `12bf99d877c33925fe0d0037f45e652d0260d8a4`; it supersedes 28ab9b2 as
the candidate to measure. The complete older release receipt is not claimed for this new source.

[Failing timing, raw reports and candidate receipts](../../plans/design-benchmarks/http-304-revalidation/)
retain all six launches, full source SHAs, actual per-report SDK/runtime/CPU and empty activity guards.

The complete v2 affinity baseline pilot still has wide Sync/Generic/Task intervals (relative half-widths
12.31%, 8.05%, 4.05%; ValueTask 1.73%). Raw three-launch evidence is retained under independent-run-precision.
The unstarted Generic-only supplement was stopped without a child or any timing. Its replacement,
the nine-launch V3 startup-affinity study, stopped after its first batch with validation exit 1.
The parser misread BDN's short binary display; the original log also records failed BDN affinity
changes after startup CPU availability was restricted. Complete raw evidence remains diagnostic.
Fresh V4 uses startup taskset only and samples the actual OS CPU sets of the host and all four
executed benchmark child PIDs, rejecting missing observations or mismatches. Nine fixed baseline
launches are pending, without pooling earlier protocols. All 31 validation-helper tests pass;
neither those tests nor these baseline pilots pass the final timing gate. [Failure, primary-source
analysis and frozen V4 protocol](../../plans/design-benchmarks/independent-run-precision/startup-affinity-validation/).
Full timing remains pending for the standalone HTTP candidate and the finished combined contract/hash source.

## HTTP 304 response-reuse directional follow-up

The first post-fix prefix/candidate pair completed on Nimbus with four cases and empty
interference guards. Candidate 12bf99d reduces allocation from 8,256 bytes to 7,536
without listeners and 7,736 with listeners, but its four means remain 2.68–9.77% slower
than prefix850 in this pair. One pair cannot prove a section-10 pass or a reproducible
regression. The earlier three-run failure is not cleared; a fresh fixed three-pair
alternating comparison is required. No timing samples are pooled across these protocols.
[Exact means, sources, raw reports and guards](../../plans/design-benchmarks/http-304-response-reuse-pair/).

## Strict body-budget allocation probe follow-up

The Linux coverage job at net10 head 7a3b9a8 failed the body-budget probe for the
method-before-generation input: expected zero, observed 7,696 bytes. Ordinary Linux
tests and the local pre-change coverage probe pass, so the original byte provenance
remains unproven. The same 100 warmups and one 1,000-call measurement now run in a
synchronous NoInlining helper on an owned thread without inherited ExecutionContext.
The exact-zero, exact sum and stored target assertions remain, with all three inputs
and no tolerance, minimum or retry. A temporary target-string regression fails all
three cases with 48,000 bytes. Production is restored byte for byte; all 320 HTTP facts
pass with coverage under default/tr-TR/sv-SE on net10. This is local fixture verification,
not current-head Linux acceptance or a performance result. The new fixture is forwarded
through #129/#130/#131 and requires fresh CI.
[Original failure, red mutation and complete green suites](../../plans/design-benchmarks/body-budget-probe-isolation/).

## Completed Nimbus preparation and native setup

The repaired PR5/prefix component harnesses now compile and pass all declared setup
combinations on Nimbus. Candidate 12bf99d also passes the eight Redis/SQL method/HTTP
setup combinations (two operations each, with origin-load assertions); both task-owned
backend containers are disposed. These are correctness prerequisites, not component or
L2 timing acceptance. [Exact sources, programs, job exits and logs](../../plans/design-benchmarks/nimbus-preparation-complete-20261015/).

A separate older #129 run at b841af2 fails two Redis subscriber-Ping setup calls on
net9.0; its net10.0 Redis integration suite passes all 15 cases present in that branch.
The original cause remains unknown. These results do not establish a repair or current-head
CI acceptance. [Original run and TRX-derived failures](../../plans/design-benchmarks/redis-subscriber-ping-observation/).

PR132 incorporates the current standalone fixtures through 9767fec in merge
2b3cb93. Both PR132 and SipHash PR133 are ready for review, and SipHash is the chosen
algorithm. The author's median improvements and regressions do not establish this
specification's independent-run confidence gate. The complete timing matrix remains
unfrozen while Claude updates the shared workloads to use GUID identifiers.

## Constructor candidate functional checkpoint

PR132 head 2b3cb93 passes all ten Linux jobs and macOS/Windows build/unit jobs, with
all three platform and aggregate statuses success: 2,519 passing facts in 16
Linux projects, zero failures and one intentional opt-in soak skip. Required native
backends, random properties, enabled soak, cultures, AOT, installed packages and docs
pass; cumulative changed-line coverage is 94.0%. This includes the current standalone
allocation fixtures. It proves the exact constructor candidate, not the standalone
head, SipHash or section-10 acceptance. The PR is now ready for review.
[Exact head, exits and original logs](../../plans/design-benchmarks/constructor-functional-2b3cb93/).

## Component harness compatibility

The prefix harness previously detected Marvin only and silently used complete-span
lookup for SipHash. An independent binary-capability guard failed before detection was
fixed. Identical preliminary workload and setup sources now pass against PR5, Marvin,
the in-place constructor and SipHash. Disabling the prefix/hash symbols fails the guard.
Local .NET 10 verification passes 92 benchmark tests, 1,036 Core tests (one intentional
soak skip), all 31 Python validation tests, the benchmark build and selected formatting.
These checks verify setup and the compiled paths, not timing. The final GUID workloads
still need integration and the combined head still needs three-platform CI.
[Red/green outputs, common sources and exact digests](../../plans/design-benchmarks/component-capability-compatibility/).

The earlier SipHash code-head 9338454 macOS job failed during Git checkout with
exit 128, before build or tests started. The cause of the unreadable tree is unproven;
this is neither a functional test failure nor a functional pass. Preserve the
[original checkout output](../../plans/design-benchmarks/siphash-macos-checkout-observation/)
and require fresh combined-head verification.

## Allocation fixture follow-up in the lower stack

HTTP head 0dae335 passes all Linux jobs and Windows build/unit jobs but fails its
macOS unit job: the old no-listener telemetry probe observes 1,880 bytes instead of zero.
The sole failed Core suite is net9.0; its net8/net10 Core suites each pass 1,022 facts.
The original byte cause remains unproven. The already-verified final owned-thread,
warmed NoInlining fixture and distinct null-key fact are now forwarded unchanged to
#129 f793d0a and #130 1b43211. Complete local net10 Core suites pass 1,023 facts in
both checkouts, with one intentional soak skip and no duplicate-ID warning.
Shipped source is unchanged, .NET 10 remains the final supported target, and new-head
CI remains required. The existing sensitivity failures remain preserved alongside
[the original 1,880-byte failure and final local green outputs](../../plans/design-benchmarks/telemetry-allocation-probe/lower-stack-0dae/).

## SipHash functional checkpoint

Exact PR133 head 83b92c6 passes all ten Linux jobs and macOS/Windows build/unit jobs,
with all three platform and aggregate statuses success. The main Linux job passes
2,520 facts in 16 projects, zero failures and one intentional opt-in soak skip.
Required Redis/SQL, 91 randomized properties, enabled soak, cultures, demo, AOT172,
installed packages and docs pass; cumulative diff coverage is 3,467/3,690 (94.0%).
This includes the constructor/SipHash runtime and final standalone fixtures. The fresh
macOS pass does not explain the earlier unreadable-tree checkout failure. PR134's newer
component harness, final GUID workloads, latest lower-stack heads and section-10 timing
are separate gates. [Exact head, exits, original hashes and logs](../../plans/design-benchmarks/siphash-functional-83b92c6/).

## Combined component-capability checkpoint

Exact PR134 head 3700ade passes all 14 Linux/macOS/Windows jobs and all three platform
and aggregate statuses. Linux passes 2,521 facts in 16 projects, zero failures and one
intentional opt-in soak skip. Required native providers, 91 randomized properties,
enabled soak, cultures, demo, AOT172, installed packages and docs pass; cumulative
coverage is 3,467/3,690 (94.0%). This includes the published component-capability fix.
It excludes the later workload-inventory tooling, final GUID workloads and timing.
[Exact source, exits and complete selected logs](../../plans/design-benchmarks/combined-functional-3700ade/).

## Compiled workload matrix preflight

The old full-series count expected 46 local cases, but the existing GUID/string additions
compile to 48 local cases plus 16 native cases. The actual inventory check failed before
the count was replaced. The harness now obtains cases from BenchmarkDotNet's converter
and exact filters, compares the complete names across revisions, and verifies every
measured name once with twelve actual workload samples. Duplicate/substituted cases and
warmup-only reports fail. The baseline f43854d and combined runtime compile identical
preliminary inventories; six preserved completed contract reports pass the new verifier.
All 40 validation tests and the CI Python suite pass (47 run, two SDK-specific skips).
Separate HTTP, net10 and constructor comparison points remain in the planned twelve-point
cohort. These checks do not freeze the final GUID source or prove timing/cache-hit behavior.
[Original failure, compiled inventories, sources and verification outputs](../../plans/design-benchmarks/compiled-workload-inventory/).

## GUID workload integration

PR135 source 0c09fc1 changes the primary contract/facade/reference inputs to GUIDs and
retains both int/GUID README comparisons. The combined integration passes all 94 benchmark
facts locally. Identical component sources pass all setup combinations on PR5, Marvin,
constructor and SipHash, preserving the direct-constructor/StartKey capability choices.
The raw hash-only component still uses :42 and is a separate int-suffix diagnostic.
Generated GUID hit/miss origin verification, full-series compilation/catalog checks and
final source freeze remain required. Earlier int timing and the preliminary 64-case
inventory are not GUID acceptance evidence.
[Exact merged sources and four-runtime setup checks](../../plans/design-benchmarks/guid-workload-integration/).

## Controlled synchronization-context probe

HTTP head f793d0a fails its macOS net10 Core job because the blocking caller thread
does not join within ten seconds. The original cause is unproven. The fixture now
keeps the blocking caller on its own recording context while the async test drives
queued transport completions, proving an incomplete outer task before release and
actual read/write participation. It retains the exact result, zero context posts and
ten-second hang guard; real transport delays and blocking the xUnit thread are removed.
A temporary capturing-continuation mutation fails with one unexpected context post;
production is restored byte for byte. All 1,036 Core facts pass under default/tr-TR/sv-SE,
with one intentional soak skip per suite. Fresh current-head CI remains required.
[Original timeout, sensitivity failure and complete local suites](../../plans/design-benchmarks/context-probe-isolation/).

## Generated GUID origin verification and final local preflight

The result-only setup guard first fails to reject an explicitly uncached service. It
now verifies eight initial origins, zero new origins across repeated hits in all eight
shapes, and thirty distinct-key misses with exactly one origin each. A repeated-GUID
mutation also fails. Origin counters execute only in factories, so every compared
revision uses the same additional miss work; earlier timings remain separate.

All twelve local revision harnesses compile the identical common workload and Program,
pass the generated origin checks and produce identical inventories: 47 local and 16
native cases. All 99 benchmark tests and 40 Python validation tests pass. This does not
prove native backend setup or timing on Nimbus; those gates and the final freeze remain.
[Actual negative tests, twelve builds/setup checks and source digests](../../plans/design-benchmarks/guid-generated-origin-preflight/).

The separate hash component now covers both Int and canonical GUID suffixes. Identical
sources pass across PR5, Marvin, constructor and SipHash, alongside all GUID facade,
buffer-growth/listener and reference-cache checks. The component matrix is 54 cases.
[Both argument kinds and exact four-runtime setup proof](../../plans/design-benchmarks/guid-hash-component-preflight/).
