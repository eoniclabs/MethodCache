# Final design verification

## Current gate and checkpoint (2026-10-17)

Latest completed confirmations (32 warmups, twelve actual iterations) narrow the remaining
cumulative timing failures to HTTP. The fixed nine-case quiet-Mac study verifies all 360
launches and passes: eight cases faster, generated GUID sync hit unchanged (ratio 0.985,
95% interval 0.963–1.007). Actual measured A2 libraries are preserved; the later A3 patch
changes only the two HTTP implementation files outside these nine call paths. The prior
Nimbus GUID slowdown remains historical platform-specific evidence, not erased or pooled.
[Complete nine-case report, provenance and raw measurements](../../plans/design-benchmarks/mac-nonhttp-warm32-v2/).

The fixed fourteen-case Nimbus HTTP confirmation verifies all 280 launches against f43854d
and **fails** on A2+A3. Four bounded cold-publication cases are 36.5–39.5% slower; listening
304 revalidation after invalidation is 6.5% slower (interval 1.047–1.083), and the other
listening 304 case has an overly wide interval (1.014–1.082). All five growing-set miss
cases are unchanged; the selected variant hit is faster. Original and accepted-rule
reviewers both fail. No HTTP time exception is added. All frozen workload source bodies,
raw measurements and verifier repair receipt are retained. The repaired collector had
selected the earlier eight-case folder; independent checks of the actual fourteen-case
sources/binaries pass, with no changed or rerun measurements.
[Complete fourteen-case failed report and evidence](../../plans/design-benchmarks/http-cumulative-time32-v1/).

The latest local code checkpoint is `f49264198631167d22d8c19e8b396d147646a87d`
(deferred local HTTP administrative IDs). Its complete 67-case cumulative cost profile
passes with the same six accepted costs, and local HTTP334/Core1047 tests pass (one
intentional opt-in Core soak skip). At collection time PR #134 was published at `2a9b47d`, with all four
statuses successful; that CI does not validate the newer HTTP patch.
Remaining work is to resolve the HTTP timing failures, publish the current evidence/code,
and run the full required CI on the actual final head. The historical observations below
retain their original source points and verdicts.


Johan replaced the historical multi-PR BenchmarkDotNet matrix with the two-step
[§10 cost-profile and paired-time gate](design-spec.md#10-prestandagrind).
The cancelled matrix's completed reports remain evidence for their original revisions;
its unmeasured comparisons are no longer outstanding work under that replaced protocol.
The dated sections below retain the investigation history, not instructions to restart it.

PR #137 is merged into #134 with merge commit
`5d9c829a857bd377049b3bd18703e97cdb254547`, whose tree exactly matches reviewed
head `8002c60b5a39fb9a5ad13d58dbaeb7bdee6960d3`. All fourteen functional jobs and
all four statuses pass: 2,598 Linux tests in sixteen projects, zero failures,
one intentional opt-in soak skip, real Redis/SQL, 93 randomized properties,
enabled soak, both cultures, AOT175, seven installed packages, demo and docs.
Diff coverage is 3527/3751 (94.0%).
[Complete job logs, exit files and original/stored hashes](../../plans/design-benchmarks/combined-functional-5d9c829/).

The fresh cost profile of that merge against
`f43854dbcd3646893f490e78a0a1d9651e9c4521` runs all 67 cases on both revisions:
**zero failures and six accepted costs**, with unchanged reasoned limits.
Redis HTTP hits use ten commands, all reads; SQL HTTP hits use two commands.
Every command/byte result, accepted reason, image/SDK/CPU receipt and source/binary
checksum is retained in the [cost-profile evidence](../../plans/design-benchmarks/cost-profile-5d9c829/).
This establishes step 1 for the combined runtime, not time acceptance.

Two subsequent timing-tool corrections leave shipped `src/` and measured workloads
unchanged. The paired reviewer rejects wide intervals before any speedup/accepted-cost
decision, unmatched launches and duplicate cases. Four tests fail before correction;
all 60 Python validation tests then pass. The exporter uses BenchmarkDotNet's already
normalized per-operation statistics once, requires twelve actual iterations and retains
those measurements plus runtime/GC metadata. Four exporter cases fail before correction;
all 120 benchmark tests then pass.
[Reviewer proof](../../plans/design-benchmarks/paired-cost-gate-validation/) and
[exporter proof](../../plans/design-benchmarks/cost-timing-export/).
Latest timing-tool head `abd0eae840e9b277358ca643a1d4ddb3d56653cf` also passes all fourteen jobs and all four statuses: 2,603 Linux tests, 120 benchmark tests, 60 Python guards, AOT175 and 94.0% diff coverage. All twenty frozen vendor image/manifest/source files are byte-identical to their actual freeze commits.
[Exact current CI and independent vendor audit](../../plans/design-benchmarks/combined-functional-abd0eae/).

The final Nimbus timing cohort is complete, with ten alternating pairs for each of the 67
main cases. Four explicitly separate generated sync/int controls (short/long prefix,
listeners off/on) receive a legacy-aware setup adapter: the old add-on must be registered
for listening to be active on f43854d. Its actual metric-flow guard fails before that
correction and passes on both revisions afterward; the earlier four controls remain
diagnostics, never pooled with the corrected controls. The normal generated `SyncHit` uses GUIDs and cannot confirm the
reported int regression. Every launch uses a fresh process; pinned images, runtime/GC,
actual affinity and interference checks apply on both sides. Work is split into bounded
capacity leases; no failed preparation or earlier timing sample is pooled into it.
An own source checkout overlapped main group 01; all of that group's original outputs
are retained as rejected evidence, and its exact four cases receive a separate complete
ten-pair reconfirmation with the same compiled files. Final cumulative timing
acceptance remains open; the two narrow SipHash time limits below are separate.

The original dispatcher stops before its first L2 launch because the SDK image
does not contain the Docker CLI used by the orchestration script. All completed
groups and the zero-launch failure are preserved: 1,831 archive entries and all
1,830 indexed hashes verify; 800 admissible original launches pass the actual
iteration, runtime/GC, affinity and interference checks. An independent continuation
uses the same frozen build outputs (444 catalogued files) and C# sources for the six unmeasured main
groups. It mounts the existing host Docker CLI read-only; client and daemon access
are verified inside the same SDK image. No host installation, image/runtime change
or shared provider modification is involved. Corrected int controls remain the
formal replacement for the known invalid original controls. No partial final
timing comparison is presented as a pass.

The complete 71-case comparison verifies all 1,420 formal launches and does not
pass. With the two authorized int limits, the strict full report has eight precise
slowdowns and 45 overly wide intervals. The 53 existing local CPU cases have
three precise slowdowns and 32 wide intervals: generated GUID sync hits rise 8.9%,
Task misses 10.6%, and ValueTask misses 11.8%. These cases receive no waiver.
An independent quiet-Mac confirmation of all three uses the same frozen compiled
baseline/final workloads; the implementation is under investigation. The ten
existing distributed-I/O cases and eight source-classified new-work cases remain
separate, with the original full report intact. L2 command-cost acceptance is
established by the independent cost profile, as specified in §10.
[Complete original/accepted reports, all raw evidence and reversible metadata](../../plans/design-benchmarks/final-paired-time-v3/).

The complete independent quiet-Mac confirmation retains all 60 launches for those
three cases. GUID sync is unchanged on the Mac (ratio 0.992, interval 0.965–1.020),
while Task misses rise 33.3% (1.319–1.347) and ValueTask misses 31.5%
(1.290–1.340). Neither machine's original result is erased or pooled with the
other. The miss regressions need correction, and the platform difference in the
GUID result remains explicit.
[Complete quiet three-case confirmation](../../plans/design-benchmarks/final-cpu-confirmation-v1/).

The miss investigation identifies redundant hashing when the memory engine removes
an evicted or expired node. `RemoveNode` already remembers the node's saved full
process-keyed hash; `EvictNode` and `OnTimerExpired` now do the same. An isolated
comparison of abd0eae against that exact two-line patch verifies all 40 launches
and improves Task misses by 15.4% and ValueTask misses by 14.6%, with both intervals
inside the precision limit. The full candidate Core suite passes 1,045 tests,
zero failures and one intentional opt-in soak skip. SipHash, its process key,
key text, equality, features and object layout remain unchanged. Separate
cumulative comparisons and the cost profile are reviewed separately; the isolated
win does not clear the earlier full-path failures.
[Exact patch, all raw launches, paired report and Core test log](../../plans/design-benchmarks/eviction-hash-reuse-v1/).

The separate complete Nimbus confirmation against f43854d verifies all sixty
launches: Task misses are unchanged (ratio 0.982, interval 0.956–1.008), and
ValueTask misses improve 5.0% (0.926–0.976). Sync GUID hits still rise 7.7%
(1.050–1.105); all three intervals meet precision, so that hit regression remains
open. The independent complete Mac confirmation also verifies sixty launches:
Task misses improve 6.3% (0.913–0.961), and ValueTask misses improve 8.6%
(0.898–0.931), while sync GUID hits are too noisy. Neither complete three-case
report passes. No miss/GUID time limit is added and neither report replaces the
original full-path evidence.
[Nimbus cumulative candidate report](../../plans/design-benchmarks/eviction-cumulative-time-v1/) and
[Mac cumulative candidate report](../../plans/design-benchmarks/mac-eviction-cumulative-time-v1/).

The candidate's complete cumulative cost profile also passes all 67 cases on each
side: zero execution errors, zero cost failures and the same six accepted costs.
All 222 build files verify before and after measurement; an independent review
reproduces the result with byte-identical accepted rules. This confirms cost
acceptance for the node-hash reuse correction, separately from the open GUID
time regression and the original insufficient-precision results.
[Complete candidate cost profile and independent review](../../plans/design-benchmarks/eviction-cumulative-cost-v1/).

The complete quiet-Mac cumulative study now verifies **53 cases, ten alternating
pairs and 1,060 fresh-process launches**. The accepted-rule report still fails:
23 cases are faster, nine unchanged, two precisely slower and nineteen too noisy.
The precise regressions are the two listening cold-HTTP publication cases,
21.9% and 24.4% slower. The corresponding non-listening estimates are also higher,
but their intervals are too wide. Task/ValueTask misses improve and the four
sync/int controls improve. GUID sync remains just outside the precision limit.
All original failed and interrupted groups are preserved; no individual launches
are removed or pooled across machines.
[Complete 53-case report and raw evidence](../../plans/design-benchmarks/mac-final-cpu53-v4/).

The complete F05 follow-up separately verifies all **thirteen predeclared cases
and 260 launches**: the twelve affected generated GUID misses and the formerly
noisy listening short-prefix sync hit. All thirteen are faster and precise;
both frozen and current-rule reviews pass. This is a thirteen-case follow-up,
not a relabelling of the prior complete failed 28-case study. The other fifteen
previously precise resident-hit cases do not execute the eviction/expiry-only
node-hash correction; actual binary provenance remains explicit.
[Complete F05 follow-up](../../plans/design-benchmarks/mac-prefix-followup-v4/).

Retained per-iteration traces reveal phase drift in the short warmup job. A fixed
eight-process diagnostic compares four and 32 warmups with identical compiled
workloads and shipping libraries. The cold workload settles after longer warmup;
Nimbus revalidation was already steady in this probe, unlike the earlier Mac
traces. This diagnostic is unpaired and supplies no acceptance decision or exact
JIT attribution. The original failed reports remain failed. A complete fixed
whole-case confirmation, selected before its means, uses 32 warmups and the same
strict six-percent precision and accepted rules.
[Warmup diagnostic](../../plans/design-benchmarks/warmup-stability-probe-v1/).

A separate instrumented HTTP storage diagnostic identifies publication as a useful
investigation target. Its wrapper changes absolute timings, so its bucket totals
are not substituted for formal performance results and do not establish causal
percentages. Commit `f4926419` defers opaque local admin-ID calculation until
inspection and memoizes against the exact immutable metadata snapshot. IDs,
removal behavior and conservative byte-budget accounting remain unchanged.
Its complete isolated eight-case comparison verifies 160 launches: all four cold
miss cases improve 5.6–7.0%, all four revalidation cases are unchanged, and every
interval meets precision. This is A2 versus A2 plus the correction, not cumulative
acceptance against f43854d. Eight semantic admin cases pass before and after;
the existing formal cold-publication gate is the prior failing regression.
Current-root tests pass 334 HTTP and 1,047 Core cases with one intentional
opt-in soak skip. No new slowdown waiver has been added.
[Instrumented diagnostic](../../plans/design-benchmarks/http-cold-stage-profile-v1/),
[complete isolated timing](../../plans/design-benchmarks/http-deferred-id-time32-v1/) and
[exact patch and functional receipts](../../plans/design-benchmarks/http-deferred-id-functional-v1/).

The correction's complete cumulative cost profile against f43854d also passes:
**all 67 cases on both sides, zero execution errors, zero failing costs and the
same six accepted costs**. All 222 build-file hashes verify before and after
measurement; independent archive and local review reproduce the terminal result.
Redis HTTP hits retain ten read-only commands and SQL HTTP hits two commands.
Temporary exact-label providers are gone and shared providers remain untouched.
This establishes current step 1 independently of timing and new-head CI.
[Complete cost profile, receipts and independent review](../../plans/design-benchmarks/http-deferred-id-cost-v1/).

The fixed post-warmup confirmation retains all 23 selected whole cases: nine
non-HTTP cases and fourteen HTTP cases. Both revisions use 32 warmups and twelve
actual iterations. The existing growing-set HTTP miss workloads retain their
request and cache-growth behavior, with an equal fixed 8,192 invocations per
iteration to bound pilot/warmup growth; they are not reset or renamed as pure
cold publication. Bounded cold-publication cases remain separate. The original
failed 53-case and older short-warmup reports remain intact. Neither a partial
case set nor an isolated optimization clears cumulative V04.

Frozen source bodies are retained per platform. Nimbus uses thirteen exact harness
files verified identical in its baseline and changed trees; the four CostProfile
files also match the exact head Git objects. Mac adds setup-only POSIX/QoS guards
and separate generated-prefix listener verification. These platform harnesses are
not claimed byte-identical, and no results are pooled across them. Each paired
comparison uses its own matching sources and recorded binaries on both sides.

The first Mac readiness attempt reserves no timing result: after 881 readiness
samples without sixty consecutive quiet seconds it is stopped before the first
launch, releasing only its own reservation. The separate whole-case confirmation
reuses the verified compiled libraries and waits for readiness before reserving
capacity, then repeats the existing guarded readiness under the bounded lease.
[Complete zero-launch readiness evidence](../../plans/design-benchmarks/mac-zero-readiness-v1/).

A separate complete helper-inlining trial retains sixty launches across the three
generated return shapes. Every interval is too wide, so the attribute change is
not adopted. The implementation retains its existing lookup-helper attributes;
no new time limit is inferred from the estimates.
[Complete unadopted trial](../../plans/design-benchmarks/hit-inlining-trial-v1/).

A second complete isolated trial caches immutable snapshot inputs to reduce
dependency-chain reads, preserving binding and live generation checks. All sixty
launches verify; sync is unchanged and the other two intervals are too wide.
This prototype is also not adopted. Neither experiment clears the GUID regression
or changes the precision criterion.
[Complete unadopted snapshot-input trial](../../plans/design-benchmarks/hit-snapshot-inputs-trial-v1/).

The completed five-case Nimbus PR128 CPU comparison retains all ten pairs per case.
Its two generated sync/int controls are faster, but the three GUID return shapes
have interval widths greater than 6%, so this comparison **does not pass** the time
gate. All 100 outputs, source/binary hashes, actual iterations and affinity/quiet
observations are verified and retained. The independently complete PR132 constructor comparison also fails precision
for four of five cases; the long-prefix int control is unchanged. The independently complete PR133 SipHash comparison has three overly wide
intervals (including the short-prefix int estimate), with the other two cases
unchanged. No bound, run exclusion or acceptance is inferred from these results. Each independent comparison is reviewed only after all its fixed
cases and pairs have completed; ongoing cohorts stay frozen.

All four corrected sync/int cumulative controls are also complete and reviewed:
three are faster, while the short-prefix/no-listener control is just beyond the
precision limit. No limit is inferred from these cumulative controls; the later
quiet-Mac PR133 evidence supplies the two narrow limits below. Original
pre-correction controls stay diagnostic.

A quieter Mac confirmation uses the same five cases and ten pairs per independent
CPU comparison, without pooling machines. Its initial setup failure reveals a
benchmark-only macOS QoS conflict. Native capability checks preserve explicit
POSIX scheduling; three regressions fail before the fix, and all 126 benchmark
cases and formatting pass afterward. Measured methods and library source are
unchanged. The final published commit still requires CI.
[Native failure, red/green, source checks and primary references](../../plans/design-benchmarks/mac-inprocess-scheduling/).
Earlier aborted Mac attempts remain separate; aggregate CPU is recorded as a
diagnostic during measurement, matching the specification's statistical gate
rather than imposing an additional threshold on ordinary desktop bursts.

The complete independent Mac PR128 confirmation verifies all 100 launches and
confirms GUID sync/Task slowdowns of 33.9%/35.2% for that earlier prefix runtime.
ValueTask and short-prefix int remain too noisy; long-prefix int is faster.
No limit is added or case omitted. The following constructor/SipHash and final
runtime comparisons remain required; this is not a claim that the final runtime
has the same regression.
[Complete Mac PR128 source, raw rows and report](../../plans/design-benchmarks/mac-cpu-pr128-time-v5/).

The complete Mac PR132 constructor comparison passes all five cases and ten pairs:
GUID sync, Task and ValueTask are 7.5%, 7.9% and 7.3% faster, short-prefix int is
6.8% faster, and long-prefix int is unchanged. Every interval meets the precision
limit. This is the actual PR132 base/head comparison, not a comparison with
f43854d; it does not by itself establish cumulative timing acceptance.
[Complete Mac PR132 source, raw rows and report](../../plans/design-benchmarks/mac-cpu-pr132-time-v5/).

The complete Mac PR133 SipHash comparison verifies another 100 launches. GUID
sync, Task and ValueTask are 18.6%, 15.7% and 14.9% faster than its Marvin base.
Long-prefix sync/int is 3.9% slower (95% ratio interval 1.021–1.057); short-prefix
sync/int has an interval just wider than the precision limit. The strict original
review exits 1. An independent ten-pair reconfirmation of that single noisy case
with the same compiled files, workload and criteria confirms +4.3%, ratio interval
1.026–1.061, with sufficient precision. Both complete raw studies and their
original verdicts remain intact.
[Complete Mac PR133 source, raw rows and report](../../plans/design-benchmarks/mac-cpu-pr133-time-v5/).
[Independent short-prefix reconfirmation](../../plans/design-benchmarks/mac-cpu-pr133-short-v1/).

Johan accepted the approximately 8% sync/int cost of choosing SipHash. Two exact
`accepted.json` time rules now cap the confirmed short/long no-listener sync/int
controls at 1.08. All existing byte/command limits and the 6% precision rule are
unchanged; GUID and other return/listener shapes receive no waiver. The complete
five-case PR133 review, using each original complete case and the complete
short-prefix reconfirmation, passes with three GUID improvements and two accepted
costs. It remains a comparison against Marvin, not cumulative acceptance.
[Original-rule and accepted-rule reports with all 100 formal rows](../../plans/design-benchmarks/mac-cpu-pr133-formal-v1/).

The explicit F05 full-path coverage receives one supplementary final comparison,
f43854d to the combined runtime: all 48 generated-prefix cost cases and 28 timing
cases (GUID sync/Task/ValueTask hits and misses, short/long prefixes, listeners
off/on, plus four sync/int controls). Ten pairs per timing case are split into
seven bounded groups. This is queued after the independent Mac CPU comparisons;
it does not restart the cancelled historical multi-PR matrix. F05 remains open.
Preparation and the complete 48-case cost phase now pass: zero execution errors,
zero cost failures, zero accepted exceptions, and unchanged costs for all 24 hit
cases. All 444 catalogued build files and both common-source copies were independently
reverified. This includes existing long-key allocations and makes no universal
zero-allocation claim. Time acceptance remains separate.
[Complete generated-prefix cost evidence](../../plans/design-benchmarks/final-prefix-cost-v1/).

The complete 28-case time phase now verifies all 560 launches: eleven precise
miss slowdowns and two overly wide intervals remain. Most generated hits pass,
but short/long prefixes and both listener states show miss regressions. The
original and accepted-rule reports are retained unchanged; no miss waiver is
added. This is the pre-correction result, not a passing final gate.
[Complete generated-prefix time evidence](../../plans/design-benchmarks/final-prefix-time-v1/).

The separate current-runtime diagnostic completes all thirty cases in fresh
processes: eight key-build/growth cases, eighteen full-span/prehashed/ready-string
lookups and four distinct IMemoryCache ready/built-key and sync/Task references.
All 54 cost cases and executable setup guards pass. These are descriptive current
components, not a paired improvement claim or a replacement for full-path gates.
[Component table and exact runtime/source receipts](../../plans/design-benchmarks/final-components-v3/).

The final source audit also adds explicit native execution of the short/long,
int/GUID and sync/Task/ValueTask hit/miss paths with listeners off/on. All 24
shape/flag cases check two independent keys and exact origin counts; enabled
listeners must record all 48 requests. JIT execution, formatting, native publish
and actual native execution pass. All twenty frozen vendor files match their
original freeze commits. Library source and measured benchmark methods are
unchanged; final published-head CI is still required.
[Complete native execution, compile marker and vendor receipts](../../plans/design-benchmarks/final-prefix-aot-v1/).

Separate cost-only profiles are complete on twelve verified PR edges (#123–#130,
#132–#134 and #137), using the same current harness and unchanged accepted limits
on both sides of each actual base/head pair. Source checkout, compilation and each
profile run are inside the bounded capacity lease. These records will identify
historical per-change failures separately from corrections in the combined runtime;
the cumulative pass is not relabelled as a pass for an earlier head.

The first per-change cost profiles are complete: PR123 passes all 67 cases;
PR124 executes all 67 on both sides without execution errors but has eight cost
failures (four listener-enabled allocation cases and four L2 command cases).
Those historical results are not relabelled as passes because the current combined
runtime passes. All twelve execute 67 cases per side without execution errors.
PR125–PR128 also pass. PR129 has six historical failures: three KeepCookies-bypass
allocation cases, Redis HTTP hit/miss command counts, and SQL HTTP hit allocations.
All 67 cases execute on both sides. These failures remain visible alongside the
later combined profile's pass; no new accepted cost is added to hide them.
PR130 also fails the inherited Redis fresh-hit write check (six writes per hit).
The read-only epoch change in merged PR137 removes that behavior; the combined
profile verifies ten Redis commands and zero writes on the same HTTP hit.
PR132 and PR133 retain the same historical Redis fresh-hit write failure; PR134's
complete per-change cost profile passes with the unchanged accepted limits.
PR137 passes too: seven profiles pass and five historical heads retain seventeen
cost failures. All 1,608 raw records, source/SDK/backend receipts and recursive
checksums for 4,200 build/runtime files are retained. Independent reviews reproduce
all twelve original verdicts; the new time-only limits change no cost verdict.
[Complete per-change profiles and exact failure details](../../plans/design-benchmarks/per-change-cost-v1/).

The final acceptance audit adds the explicit G05 ordinary-late-DI control to the
existing late-cacheable rejection fact. All 19 Quick Start cases and formatting
pass locally; production and timed workloads are unchanged. This test addition
still needs the final published commit's CI.
[Source/check receipts](../../plans/design-benchmarks/final-g05-acceptance/).

PRs #123–#133 and #135 have all four successful statuses on their current heads,
verified on 2026-10-16. #134 remains draft until final evidence review. The reported
double-timer allocation in `DistributedCacheLayer.TryRunAsync` is unchanged; no
unmeasured optimization or release/API promotion is included.

The [76-row acceptance inventory](../../plans/design-benchmarks/final-acceptance-inventory-v1/)
maps every explicit specification row to inspected assertions, source hashes and
its evidence. It distinguishes the green published functional checkpoint from
candidate-only checks and the still-open timing/final-commit gates; the inventory
is not a claim that all acceptance rows pass.

Published candidate `f752037547652c3c87b5899efb17733580b72103` reveals a Windows
checkout failure before compilation: the supplementary evidence directory is
named `aux`, a reserved Windows device name. It is renamed to `additional` with
all 375 file-content hashes unchanged. The manifest records that mapping, and
the portable restorer verifies all 3,168 original archive file hashes. A new
tracked-path guard fails before the rename and all four checks pass afterward;
the existing build job discovers it automatically. The new published commit
still requires all three platforms; running jobs and providers remain untouched.
[Actual Windows errors and red/green proof](../../plans/design-benchmarks/windows-path-guard-v1/).

The Windows console snapshot also exposes Git line-ending normalization: its
working-file hash matches the manifest, but the initially staged log does not.
Evidence paths now disable text conversion in `.gitattributes`; ordinary source
rules stay in effect. A regression using Windows Git settings fails before that
rule and passes afterward for logs, JSON/JSONL, Markdown, patches and PowerShell.
The original console bytes are restored in Git. The complete published batch is
also checked against Git objects, rather than working files alone.
[Line-ending regression and complete Git-object verification](../../plans/design-benchmarks/evidence-line-endings-v1/).

Published `be88349fae9b2e0c55406ccde578a0d1a105b233` passes all fourteen jobs and
all four statuses after those checkout/evidence corrections. Earlier complete
CI attempts remain failures: f7520375 reports 4,480 bytes in the Docs public-hit
probe's first measurement, and d3126b71 reports a Redis deadline assertion failure
during the coverage run. Neither intermittent failure is silently discarded.

The Docs probe now warms its complete counter/loop path before one unchanged
fixed window requiring zero bytes. A negative control detects one escaping
allocation, and actual listener callbacks and the single origin load are
asserted. Ten fresh original-code processes pass, as do ten candidate processes
with both facts and all 120 Docs tests. The Core DLL is byte-identical; this is
test initialization, not a library allocation waiver or a proven attribution of
the original transient bytes.
[Original CI failure and complete probe validation](../../plans/design-benchmarks/docs-hit-probe-v1/).

The deadline-only native phase also detaches and drains callbacks before advancing
its fake clock. Recovery is allowed to retire a running load; earlier native
mutation/delivery assertions stay active. Two deterministic barrier facts fail
before the change: a queued callback enters after unsubscribe and unsubscribe
returns before a running callback completes. All three barrier facts then pass.
Ten fresh original Redis processes pass; ten candidate Redis and three SQL Server
processes, with coverage collection enabled, also pass without skips. All 1,047
Core tests pass with the intentional opt-in soak skip, and Core DLL hashes are
identical. Deadlines and value assertions are unchanged. These test corrections
still require CI on their published commit; neither claims a precise scheduling
explanation for the earlier native failure.
[Actual native failure, deterministic fault tests and all native results](../../plans/design-benchmarks/policy-deadline-isolation-v1/).

The latest published functional checkpoint is now
`3e9df33eb9da29c7740b2cd4434345a4486a62ce`: all fourteen jobs and all four
statuses pass, with 2,612 Linux tests, zero failures, one intentional opt-in
soak skip and 3,529/3,753 changed lines covered (94.0%). This includes both
test-support corrections described above, all 120 Docs tests and 1,047 Core tests.
It supersedes pending functional-checkpoint statements above; timing remains open.
[Exact complete job logs, statuses and original/stored hashes](../../plans/design-benchmarks/combined-functional-3e9df33/).

A diagnostic changes only the memory engine's comparer selection, retaining the
current generated binding, generation checks, telemetry and node-hash correction.
The complete initial three-case study is too noisy on all three return shapes.
An independently declared twenty-pair sync-only confirmation verifies all forty
launches and measures the runtime's adaptive string lookup 9.3% faster than the
current SipHash prefix lookup (ratio 0.907, 95% interval 0.884–0.930), meeting the
precision criterion. This attributes the difference to the hash/lookup mechanism
as a whole; it does not isolate the SipHash primitive alone. The original runtime
explicitly uses ConcurrentDictionary's non-randomized string fast path; .NET 10
retains an adaptive comparer for default/ordinal strings, as documented in the
[primary runtime source](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Collections.Concurrent/src/System/Collections/ConcurrentDictionary.cs).
Production keeps SipHash. No new acceptance rule is inferred from this diagnostic.
[Original complete diagnostic](../../plans/design-benchmarks/guid-hash-control-v1/) and
[independent precise sync confirmation](../../plans/design-benchmarks/guid-hash-control-sync-v1/).

The fixed quiet-Mac cumulative confirmation now runs all 53 existing local CPU
cases, ten alternating pairs each, in fourteen bounded groups. Its compiled
inputs match every current production source file; the exact original SHA and
two-line correction remain explicit. No group means are reviewed before the
entire fixed cohort terminates. Afterwards, a queued F05 follow-up covers all
twelve GUID miss shapes affected by node removal plus the one previously noisy
listener-enabled sync hit. The other fifteen previously precise F05 hit cases
retain their complete original results; the four int controls are also included
in the current 53-case cohort. Both cohorts reuse the same compiled files and
frozen workloads, without new checkouts or builds during timing. These are final
confirmation requirements, not the cancelled historical multi-PR matrix.

The quiet-Mac final cohort subsequently stops during CPU-04 pair 7 when its
predeclared process guard reports a competing build/test command. Groups
CPU-00–03 are complete and retained without reviewing their means. A continuation
using the same 444 compiled files replaces the entire interrupted four-case
group, then is itself stopped by the same guard during pair 6. Both interrupted
attempts are preserved and contribute no formal timing records. Their following
F05 queues start no measurements. External process identity is under observation
before another attempt; no other job or provider is stopped. The remaining fixed
cases and full-cohort review are still required.
[Both complete interrupted attempts and queue diagnostics](../../plans/design-benchmarks/local-final-interference-v1/).

## Historical checkpoints and investigations

## Historical 908a94e three-platform functional checkpoint

Combined head `908a94efa23fe27629ed9f00070a6bbd116740d0` passes all fourteen CI jobs
and all four commit statuses. Linux runs 2,562 facts across sixteen projects, with no failures
and the single intentional opt-in soak skip. Required Redis/SQL, randomized properties,
enabled soak, both cultures, demo, AOT175, all installed packages and docs pass; changed-line
coverage is 3486/3708 (94.0%). The complete no-listener probe passes on all three platforms.
The preceding 453be95, 5dd27bb, dd6b1c6 and 6caa856 also pass all fourteen jobs. These results do not explain the historical
272-byte object's exact source or establish timing acceptance. Subsequent benchmark-tool, test and
documentation edits require their own published-head CI.
[Exact logs, exits, source hashes and commit statuses](../../plans/design-benchmarks/combined-functional-908a94e/).

Source review before full timing also corrects the archived V4 analysis helper's L2 label:
enabled round-trip validation already existed before PR4. The new opt-out changes writes;
all measured L2 reads remain comparable. Use the corrected reviewer under benchmark validation,
retaining the original archived helper and its failed classification as historical evidence.
Six assertion subtest failures reproduce the error before correction; all twelve summary tests
and all 52 benchmark-validation Python tests pass afterward locally and on Nimbus. The deployed measurement protocol,
workloads, process order and sample counts are unchanged. This correction grants no timing waiver.
[Fourteen exact source checks, statistics defaults and red/green evidence](../../plans/design-benchmarks/guid-feature-review-v1/).

## Frozen inline vendor coverage

The historical contract-1 vendor images target .NET 8 and exercise materialized
keys. A metadata coverage regression proves that those images do not reference the
new inline KeyBuilder constructor. A separate .NET 10 checkpoint now covers that
ABI without replacing any original image or manifest. All nine compatibility facts
pass; a temporary removed-constructor mutation fails with MissingMethodException,
and the extended native osx-arm64 smoke publishes and runs successfully. The explicit
freezer refuses existing manifests. No shipped library source changes. Final-head
cross-platform CI remains required.
[Exact provenance, sensitivity proof and native receipts](../../plans/design-benchmarks/net10-inline-vendor-baseline/).

A separate full-surface guard captures all 103 Generated declarations from the
original PR1 source and detects the StartKey member removed by #132. Its restoration
keeps the constructor path used by current generated code. Ten compatibility facts,
1,043 Core facts, 100% coverage of the changed executable line and native osx-arm64
smoke pass. Neither frozen checkpoint is replaced. Existing constructor-path timing
workloads remain unchanged; the legacy factory copy cost is a distinct caller path.
Final-head CI remains required.
[Frozen API declarations and exact red/green/coverage proof](../../plans/design-benchmarks/generated-api-preservation/).

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
pinning all 138 DLLs per side. All eighteen launches now complete, but all four cases
still have insufficient precision. Point changes are −20.41%, −15.04%, −0.51% and −20.01%;
wide 95% intervals establish no speedup or timing acceptance. BDN retains twelve Actual
measurements per case and applies its existing outlier filter to each process mean; every
launch remains included. The CPU2 cohort takes substantially longer than the earlier
unrestricted cohort, with no established cause. Section 10 stays open.
[Complete nine-pair results, original measurements and provenance](../../plans/design-benchmarks/http-304-header-affinity-confirmation/).

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
Both pass all four exact-head commit statuses; the old 0dae335 macOS net9 failure is retained below.

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

## Generated benchmark program compilation

The V4 attempt executes zero benchmarks. Its first batch reveals that an asynchronous iteration
setup compiles in the harness but fails in BDN's generated program (CS0407). A delegate-contract
regression fails before the correction; all 113 benchmark tests pass afterward. The iteration
boundary is now void and waits for the unchanged asynchronous reset outside the measured cache call.
The new build-only gate also finds the component project's filename/assembly-name mismatch; its
factory regression fails before the rename. The actual corrected generated programs compile all
67 primary cases (including four untimed diagnostics) and 102 component cases locally. All 53
Python validation tests pass. Every runtime point must pass the new generated compilation gate
before a fresh cohort; no rejected V4 sample is carried forward.
[Original failures and concrete red/green evidence](../../plans/design-benchmarks/guid-full-bdn-build-failure-v4/).

## Corrected V5 preparation and dispatch

V5 retains the fourteen runtime points and six component C# points, with primary workload source
453be95 and component source bbff4a6. All twenty actual BDN-generated programs compile: each
primary program contains 67 cases, four of which are untimed growing-cache diagnostics; each
component program contains 102 cases. Origin/setup guards pass. Each primary runtime then passes
fresh Redis/SQL setup with actual pinned image IDs verified before and after execution.

All sixty immutable compiled-family validations pass. Independent review at 09:09:35 UTC checks
the actual generated build logs, fourteen native receipts, exact source/program bytes and
2,358 harness plus seventeen native-runner DLLs. The fixed 180-launch series explicitly starts
at 09:10:18 UTC on 2026-10-16. The first benchmark processes execute and exit successfully;
no partial means are reviewed or pooled. Its first twelve family launches complete successfully.
At 11:20:31 UTC the original controller is stopped only after the twelfth worker and
owned cleanup have finished. The source-scoped V6 schedule starts at 11:21:08 UTC,
reusing eight whole launches and preserving four others as diagnostics. It selects
153 family launches / 3,690 cases, retaining all eleven original comparisons with
unchanged measurement settings and acceptance criteria. The complete cumulative
comparison is prioritized. Selection uses source changes before any timing means
are assessed; missing or uncertain results are not passes.
[Selected coverage, immutable controller and transition receipts](../../plans/design-benchmarks/focused-schedule-v6/).
This dispatch was later cancelled when Johan replaced §10; its completed evidence remains historical.
[Exact frozen inputs, native receipts, build logs, validation and dispatch](../../plans/design-benchmarks/guid-full-preparation-v5/).

## Remaining acceptance gates under the current §10

- V01–V03: collect latest published-head functional CI and verify generated documentation,
  packages/AOT, public APIs and unchanged original vendor fixtures. Older checkpoints
  above establish their own source versions only.
- V04 step 1: the combined runtime's 67-case cost profile is complete and passes with six
  explicit accepted costs. Per-change evidence must identify its actual base; historical
  failed or incomplete time series remain historical rather than being relabelled passes.
- V04 step 2: finish the one final paired comparison against f43854d on a quiet machine,
  review every interval, and retain required filtered checks for CPU-only changes.
  An interval wider than 6% fails even when the point estimate is faster or deliberately
  accepted. Add a reasoned sync/int SipHash limit only after confirming that exact workload.
- Publish the final evidence and update the PR descriptions around the final state.
  No merge of the design series into its base or package release is authorized here.

The earlier V4/V5/V6 multi-PR schedules are cancelled and do not remain merge conditions.
Their source/selection/preparation/failure receipts remain available for audit. Current
commands and criteria are defined in §10 and the cost-profile README.

Exact current-head functional receipts are now collected for [HTTP f0dd6c2](../../plans/design-benchmarks/http-functional-f0dd6c2/),
[.NET 10 1bafff1](../../plans/design-benchmarks/net10-functional-1bafff1/) and
[SipHash 4f6cd73](../../plans/design-benchmarks/siphash-functional-4f6cd73/): fourteen job exits and all four
commit statuses pass for each. HTTP's historical branch still ran three frameworks; that does not
expand the final .NET 10 support target. Combined-head CI and timing are separate remaining gates.

The [frozen version-4 protocol sources](../../plans/design-benchmarks/guid-full-protocol-v4/) retain all
180 planned launches and the actual PR comparison edges. Preparation and all sixty compiled-family
validations complete before the independent input review and explicit dispatcher launch. The series
stops at its first generated-program compilation. Setup/inventory passes alone were insufficient;
a new corrected cohort and complete section-10 review remain required.

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
