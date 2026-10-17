# PR 1: generated contract validation

Implementation base: `a023336088c0df4fe887ee336dabcbbdb41f5b26` (merged PR 122).
The specification is [design-spec.md](design-spec.md), PR 1 acceptance G01–G14.

| Acceptance | Evidence |
|---|---|
| G01 | CompatTests references checked-in vendor/third-party DLLs, verifies manifest hashes and executes sync/Task/ValueTask, tags, invalidation, fallback, generics and CacheInterface. |
| G02 | Contract99 DLL's real module initializer loads successfully; AddMethodCache rejects version 99 before Describe runs. Core also checks bootstrap directly. |
| G03 | SourceGeneratorTestEngine checks semantic Core symbols in generated sources throughout SignatureCorpus. |
| G04 | FinalVendorCompatibilityTests executes the frozen vendor in two containers with separate managers, overrides and disposal; keyed registration and ownership tests exercise DI lifetimes. |
| G05 | QuickStartRegistrationTests and startup validation cover late cacheable registration and repeated AddMethodCache. |
| G06 | QuickStartRegistrationTests covers explicit opt-in, zero/two candidates, scoped auto-registration and explicit factory lifetimes. |
| G07 | AotSmokeTest includes a separately compiled vendor assembly and explicit FromAssemblyOf selection, as well as explicit AutoRegister. |
| G08 | KeyedRegistrationTests and existing refresh/fail-safe scope tests preserve exact inner registrations, keys and background scopes; compat tests cover keyed closed generics. |
| G09/G12 | GeneratedContractTests gates origin execution, retires captured generations, rejects foreign bindings and protects publication for both changed and unchanged key versions. |
| G10 | Actual packed nuspecs require exact matching versions; check_package_restore.py installs both reference orders and rejects a higher direct Core version with strict NU1608 for all six add-ons. |
| G11/G13 | SignatureCorpus, canonical key properties, Unicode/fallback vendor cases, buffer growth and exact dirty-buffer tests. |
| G14 | SignatureCorpus across supported TFMs and compiler versions; all 175 supported corpus cases compile with Native AOT, plus executed frozen vendor/smoke shapes and GeneratedContractBenchmarks. |

## Contract and scope

The original `Contract1` and `Contract1Net10` checkpoints are historical development evidence, retained unchanged.
PR134 review deliberately narrows the still-unpublished Generated API before 1.0: inline generation always uses the
bound `KeyBuilder` constructor and `AppendPart`, with ref lookups. `MethodCall.StartKey`, `BindKey`, `KeyPrefix` and
`TextKey` remain implementation details. No released binary contract is being changed.

`Contract1Final` is the explicit pre-release freeze for that reviewed surface. Its vendor exercises primitive,
collection, record, provider and fallback keys, generic/version keys, all three return shapes and invalidation.
The compatibility suite also checks unknown-module selection and two containers with separate managers, overrides
and disposal. Frozen images and the exact Generated API manifest are produced only by the explicit freeze command;
normal builds never regenerate them. Final freeze/test receipts are recorded in the current review validation.

Module initializers register descriptors only. Startup validates contract versions, selects modules and binds each
service registration to its own runtime context. One immutable MethodCall snapshot supplies key construction,
lookup, tags and load/publication policy. Inline and materialized keys retain snapshot identity; retired snapshots
bypass and retired loads cannot publish under a current generation. Generic argument metadata is static per
compiled type combination; snapshot specialization uses weak keys owned by the invocation snapshot.

Synchronous methods use a synchronous L1/origin path. Async callers can join synchronous owners; cancellation and
invalidation do not let a late owner replace current data. Recursive factories fail instead of waiting for
themselves. OpenTelemetry retains generated hits and synchronous miss/invalidation tracing through an internal
capability, while arbitrary async managers bypass synchronous caching. Current L2 adapters have no bounded native
synchronous operations: sync reads use L1/origin, and sync distributed mutations report unsupported invalidation.
Use async mutation methods with distributed storage.

Public API slimming, final policy fingerprints/options, transport/wire changes and the instrumentation rewrite
remain PRs 2–5. Prefix hashing remains its separate later PR. No hash algorithm change is included here.

## Repeatable checks

```bash
dotnet test MethodCache.sln -c Release
build/ci/jobs.sh build diffcoverage cultures properties soak
dotnet pack MethodCache.sln -c Release -o artifacts/packages
python3 build/ci/check_package_contracts.py artifacts/packages
python3 build/ci/check_package_restore.py artifacts/packages
build/ci/jobs.sh packages
```

On macOS, Native AOT is published and executed with `--runtime osx-arm64`; the Linux CI job uses linux-x64.
The initial vendor baseline is created explicitly by freeze_generated_contract.py after committing its sources;
ordinary builds and tests never regenerate it. Its manifest records the implementation source commit and packed
Core/generator provenance independently of later runtime commits.

## Performance evidence

Historical samples and run means (external archive `review/legacy-measurements/design-pr1-benchmarks.json`) compare the merged PR 122 base with this implementation
on one Apple M5, macOS 27.0.1, .NET 10.0.3, SDK 10.0.103, Concurrent Workstation GC. Each revision was measured in
three separate processes, alternating base/change/change/base/base/change. Both revisions used identical workload
source and the same macOS thread QoS setting. Full runs used five warmups and ten measurements; focused confirmation
runs used six warmups and fifteen measurements. The intervals below are 95% Student t intervals across independent
run means, not BenchmarkDotNet's within-process intervals.

| Workload | Base, ns ± CI | PR 1, ns ± CI | Base → PR 1 allocated bytes |
|---|---:|---:|---:|

| SyncHit | 37.37 ± 1.50 | 33.91 ± 1.86 | 0 → 0 |
| GenericHit | 56.21 ± 1.40 | 47.75 ± 0.21 | 224 → 0 |
| TaskHit | 37.59 ± 1.70 | 34.15 ± 0.28 | 0 → 0 |
| ValueTaskHit | 38.78 ± 2.18 | 36.87 ± 1.29 | 0 → 0 |
| RecordHit | 77.91 ± 3.31 | 79.29 ± 2.65 | 296 → 296 |
| CollectionHit | 68.54 ± 2.75 | 64.40 ± 1.61 | 384 → 256 |
| FallbackHit | 219.15 ± 6.42 | 220.76 ± 7.24 | 408 → 408 |
| SyncMiss | 442.43 ± 7.15 | 415.09 ± 6.99 | 1048 → 928 |
| ValueTaskMiss | 456.20 ± 27.65 | 466.86 ± 2.62 | 1056 → 1080 |
| TaskMiss | 439.60 ± 11.16 | 450.09 ± 2.56 | 1048 → 1072 |

The first full ValueTask series had a wide interval, so a focused three-plus-three series resolved the uncertainty.
Collection keys failed that confirmation (69.25 ± 2.25 → 73.88 ± 1.48 ns, +6.7%); these rejected samples remain in
the evidence. Array formatting was then changed to format each element once and write the final string directly.
The last three-plus-three collection series above passed, with identical canonical key text and fewer allocations.
Other workloads use the full series; that formatter change affects array collections only.

No measured hit shape allocates more. Async miss means rise 2.3–2.4%, below the specification's 3% stop threshold;
record/fallback means rise less than 2% with overlapping intervals. These are local regression measurements, not
portable product speed promises. README product benchmark numbers are unchanged.

## Final verification

- Complete .NET 8/9/10 solution test matrix: 6,413 passed, three opt-in soak skips; real Redis and SQL Server tests required and executed.
- Turkish and Swedish culture runs passed. Random-seed properties passed on .NET 10 and .NET 8, five seconds per property. The 30-second soak run passed.
- Diff coverage against the implementation base: 738/800 changed production lines, 92.2% (80% minimum).
- Frozen vendor baseline records implementation source `9294adb3bd562b502d18a428d452b0bc40199515`, real package `1.0.0-g9294adb3bd`, Core/generator/source/image hashes. All four binary compatibility tests passed on all three TFMs after freezing; the manifest test rejects placeholder provenance.
- Native AOT osx-arm64 smoke passed with synchronous, Task, ValueTask, generic method metadata, closed generic interfaces, CacheInterface, tags, invalidation, records, providers and collections. All 172 supported corpus cases were additionally published as Native AOT with every scenario call reachable to the native compiler. Reflection-based fallback retains its documented trim/AOT warnings and runtime limitations.
- Demo passed. Generator snapshots, canonical Unicode/collection properties, binding and retirement race tests passed. Both telemetry sync caching and cross-shape recursion tests were observed failing before their respective fixes.
- Python CI tests: 42 run, two SDK 10.0.300+ publishing tests skipped on local SDK 10.0.103. This PR does not alter publishing.
- Actual seven NuGet packages packed and validated. Exact Core dependency ranges passed for all six add-ons; both matching reference orders restored, and all six direct Core mismatches failed strict NU1608 restore. Quick Start passed with SDK 10.0.103 and SDK 8.0.203. All six add-ons passed installed-package app tests on net8.0 and net10.0 with real Redis and SQL Server.
