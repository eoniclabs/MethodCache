# PR 2: user API and policy isolation validation

Stack base: `8abc020b8f118331b1c5b74d676ae0957af1b91e` (PR 123).
Implementation: `e5dc3b8b93c417f453440e6ff8ffe79405f092f7`.

Scope: [design-spec.md](design-spec.md), PR 2 acceptance P01–P16. Generated contract 1 signatures and
frozen vendor/third-party fixtures remain unchanged.

| Acceptance | Evidence |
|---|---|
| P01 | `PolicyTransitionTests.BehaviorChange_RetiresPendingLoads_BeforeReturning` gates loads for duration, sliding, refresh, fail-safe duration/timeout/retry, tags/clear-tags, version, Distributed, CacheWhen and Enabled. Existing generated contract and background scope tests cover old snapshots, refresh and publication winners. |
| P02/P05 | `PolicyTransitionTests` and shared `PolicyBackendContract` exercise independent private namespaces, their expiry, a new container, override removal and a cold shared reader. |
| P03 | `PolicyIdentityProperties`, `PolicyHandleTests` and the audit/predicate no-op test verify canonical tag/order independence and unchanged generation for equal effective behavior. The final shared native acceptance case additionally reverses two non-conflicting configuration sources and tags, proves cross-node L2 reuse and retains generation/identity after audit-only changes. |
| P04 | `ConfigReload_NewIdentity_CannotReadOldConfigurationWrites` checks synchronous reload publication and rejection of old-policy writes. The final shared native case waits for the actual owned invalidation completion, then proves old-policy writes are excluded and equal reloaded policies share L2. Existing configuration/precedence tests preserve specificity. |
| P06 | `PolicyIsolationTests` and the real-provider contract check slow factories, first publication time, remaining lifetime, null rejection and sliding/fail-safe source deadlines. |
| P07/P08 | Resolver, configuration and disabled-generation tests cover specificity, bypass, old loads and re-enabling. Final model/Redis/SQL cases cover explicit attribute Enabled over group/file defaults, eight unjoined bypass calls, held origin and held background refresh, zero value-write attempts after disabling, cold-reader rejection of old work and re-enabling. |
| P09 | `FourMethodManagerTests` is compiled without friend access; generated calls and bulk use only the four public operations, immutable options, version helpers and typed predicates. NoOp and sync bypass have regression coverage. Final model/Redis/SQL configurations exercise asynchronous recording/NoOp managers and the synchronous bypass without invoking those managers. |
| P10 | Sync contract tests and provider smoke tests preserve native synchronous factories. Current Redis/SQL adapters expose no bounded synchronous capability: L2 is bypassed with diagnostics, never by waiting on an async loader. Final provider cases prove actual async cross-node sharing alongside local-only sync results; a held async read over each actual provider must remain pending while sync calls complete with zero synchronous backend reads. |
| P11/P12 | `InvalidationResultTests` checks exact partial counters, independent storage/publication queues, recovery, malformed/duplicate/empty batches and cancellation before/after mutation. The final descendant additionally links `NativePolicyFaultContract` unchanged into the model, Redis and SQL projects: successful stamp/publication operations reach the real provider, exact 3/3/2/1/1/1 counters and drained queues are checked, and confirmed stamp bytes are read from that backend. |
| P13 | `LostMethodMessage_LeavesRemotePolicyAndL1UntilExpiry_WithoutCrossingPrivateNamespace`, chaos tests and health checks document and exercise best-effort delivery. Real-provider contract checks typed method delivery without changing the receiver policy. Final tests keep the native subscription connected, explicitly observe/drop its method frame, and verify unchanged receiver generation/policy, permitted stale L1 until its own deadline and correct expiry. |
| P14 | Options snapshot/shape tests, predicate no-op identity tests, L2 null/predicate rejection and reserved key/tag injection tests. |
| P15 | Existing bulk concurrency, publication/invalidation, bounded L2 parallelism and presence tests; public forwarder and synchronous manager failure regressions ensure custom managers are not bypassed and pending loads are released. |
| P16 | API analyzers, `UserApiShapeTests`, generated contract tests and compiled documentation verify presence-aware lookups and cancellation last. |

Fault injection remains deterministic and runs against the in-process atomic store. Both real Redis and SQL
Server execute the same shared policy/deadline contract; their existing integration suites cover transport,
reconnect, tombstone and provider failure behavior. This is not a claim that every chaos fault is injected
into both external database implementations.

## Deliberate staging boundaries

- PR 3 owns registration/attribute surface cleanup; PR 5 owns metrics-provider removal.
- PR 6 owns independent HTTP storage. `MaxHttpBytes` is present in the final options shape but is explicitly
  documented as reserved until that store exists; this PR does not claim it limits current HTTP storage.
- The minimal entry format 5 timestamps, policy namespace and typed method message are implemented now
  in both adapters, because policy isolation cannot depend on future PR 4 changes.
- `CacheEntryOptions.GetCacheKey` and `CanCache` let four-method custom managers honor prepared generated
  keys, versioning, Enabled and typed predicates without private runtime knowledge. Admin/override options
  remain reusable for manual calls.

## Regression evidence

The audit-only generation, L2 null/predicate admission, configured-unknown method, L2 sliding/fail-safe deadline,
and synchronous custom bulk failure tests were observed failing before their respective fixes. Timing behavior
uses `FakeTimeProvider`; the bulk failure test uses a wall-clock timeout only as a deadlock guard.

## Local validation

- Release test matrix: **6,542 passed**, no failures, three intentional soak skips, across 46 project/framework runs (.NET 8/9/10). Redis and SQL Server integration tests ran against real containers.
- Packed all seven packages; exact-version dependency checks passed. Core and all six add-ons passed package smoke tests with the current SDK and the real **8.0.203 SDK**, including Redis/MessagePack, SQL Server, ASP.NET Core, OpenTelemetry and HTTP.
- Native **osx-arm64** smoke test and all **172** signature corpus cases compiled and executed. The Linux-specific CI command cannot run on this macOS host because its cross-compiler requires `objcopy`; Linux/Windows remain CI checks. Existing fallback-key reflection/AOT warnings are retained and are not presented as a general guarantee for arbitrary reflection-based serializers.
- `cultures properties soak` passed: tr-TR/sv-SE, randomized CsCheck runs on net8/net10 and a 30-second concurrent soak. The ordinary matrix keeps soak opt-in.
- Full solution formatting, compiled documentation/snippet generation and generated LLM documentation were run.
- A full-matrix run exposed a concurrent list read in the HTTP meter test; assertions now use the writer's lock. A Redis sharing assertion also exposed a first pub/sub handshake race in test setup; the connected-node fixture now establishes that connection before attaching reconnect invalidation. The final complete matrix passed after these test fixes.

- Diff coverage against the exact stack base: **671/748 = 89.7%**, above the 80% gate.
- Guided console demo passed. Gitleaks reported no secrets before the implementation commit.

## Performance

Paired measurements use the frozen implementation commit above. The extra closure in the custom-manager
fallback was split away from built-in calls. Distributed load timestamps now live in optional state, preserving
the original memory-flight footprint; this is checked with the same generated workloads and allocation counters.

The first exploratory series overlapped the automatically started local macOS CI build after PR publication;
its timings are excluded. A short isolated series then showed unstable fallback-key timings, so longer warmups
are used for confirmation. No speedup is inferred from those unstable numbers.

The initial implementation commit `5bacd3d264009464ac552c68002b3a41ea527a71` passed all external Linux,
Windows and macOS jobs, including Linux AOT, packages, cultures, properties, soak and diff coverage. The later
optional timestamp-state change passed the full local matrix, package/AOT checks and updated coverage above;
the final PR head still requires its own external CI result.


[Machine-readable measurements](design-pr2-benchmarks.json) retain the individual iteration values and run
means. Apple M5, macOS 27.0.1, SDK 10.0.103, runtime .NET 10.0.3, Concurrent Workstation GC, no telemetry
listeners, default no-op metrics provider. Both revisions use the identical generated workload and QoS setup;
MaxItems=1024, fixed warmed hit keys and increasing miss keys, no L2. Run order is base1/change1/change2/base2/base3/change3.
The full series uses six warmups, 15 measured iterations of 100 ms; fallback confirmation uses 15 warmups,
20 measured iterations of 200 ms. Each comparison has three independent runs per revision.

Values are nanoseconds, mean ± 95% Student t interval of the three run means (t=4.303, df=2).

| Workload | PR 123 | PR 2 | Mean change | Allocated B/op, base / change |
|---|---:|---:|---:|---:|
| SyncHit | 33.51 ± 1.98 | 33.42 ± 0.83 | -0.25% | 0 / 0 |
| GenericHit | 47.97 ± 0.34 | 48.78 ± 1.32 | +1.69% | 0 / 0 |
| TaskHit | 35.14 ± 3.78 | 34.17 ± 0.59 | -2.78% | 0 / 0 |
| ValueTaskHit | 37.32 ± 0.86 | 37.32 ± 1.47 | -0.01% | 0 / 0 |
| RecordHit | 79.20 ± 1.87 | 80.23 ± 4.39 | +1.31% | 296 / 296 |
| CollectionHit | 66.42 ± 4.28 | 65.83 ± 3.28 | -0.89% | 256 / 256 |
| FallbackHit (long warmup) | 223.48 ± 5.19 | 220.41 ± 2.95 | -1.38% | 408 / 408 |
| SyncMiss | 410.01 ± 7.27 | 410.44 ± 4.85 | +0.10% | 928 / 928 |
| ValueTaskMiss | 459.92 ± 2.61 | 463.24 ± 1.45 | +0.72% | 1080 / 1080 |
| TaskMiss | 473.04 ± 12.71 | 463.99 ± 24.31 | -1.91% | 1072 / 1072 |

Allocations match for every measured workload, including all three miss shapes. The longer fallback run
supersedes the unstable short fallback timings; it is not evidence of a large speedup. Other wide intervals
remain uncertainty, so these results are not a blanket pass for the complete section-10 gate.

## Remaining merge checks

The generated-path measurements do not replace the complete cross-feature matrix in design spec section 10.
In particular, strict L2 removal validation adds a metadata read before admission; its backend latency/round-trip
cost must be measured on Redis and SQL Server with the same backend conditions before merge. HTTP, pressure,
contention and listener scenarios also remain part of that broader gate. The PR stays draft; the functional
matrix and unchanged Generated ABI are not presented as proof that every section-10 scenario passed.

The d4a150a external pass completed on Linux (all jobs) and Windows. macOS exposed a race in `OneSlowWrite_DoesNotTurnOffL2ReadsOfOtherKeys`: its fake clock advanced before the delayed write necessarily started, timing out at line 404. The test now waits for the fault-start barrier before advancing time, matching the neighboring shutdown test. The CI failure is the red-before-fix evidence; no production timeout or cache behavior is relaxed.

A heavily loaded three-framework run during PR 3 also reproduced the existing cancellation test racing its 100 ms origin delay (`value|v1|1` instead of `cancelled|v1|2`). Its first origin is now held by an explicit completion-source barrier until cancellation; the surviving caller retries independently. This removes the wall-clock assumption without changing production cancellation behavior.
