# Design-series performance evidence

`create-harness.py --runtime-root CHECKOUT --output OUTPUT` copies identical public workload sources into an
external BenchmarkDotNet project. Build that project in Release, invoke its DLL with `--verify-generated`, then
run `--verify-native` before timing. The generated guard checks eight initial origin calls, repeated resident
hits without new origins, and thirty distinct-key misses. Origin counters run only inside factories; all
revisions use the same instrumented workload and earlier measurements remain separate.
Real Redis/SQL connection variables are required for native setup; it checks the HTTP storage mode, repeated real L2 reads,
origin loads and rejection of L1 admission. Older HTTP revisions explicitly enable their separate L2 option.

`create-native-driver.py --output OUTPUT` creates an external test-container runner. Build `Runner.csproj`, then run
its DLL with a working directory followed by the child executable and arguments. It supplies fresh Redis 7 and SQL
Server 2022 containers and credentials through the child's environment, checks its exit code and disposes its own
containers. It does not print credentials or touch a production backend.

`run-series.py` expects twelve prepared harnesses under `/tmp/methodcache-design-harness` and the built driver at
`/tmp/methodcache-http-native-runner/bin/Release/net10.0/Runner.dll`. These paths are configurable as constants at the
top of the script. Each manifest records absolute runtime roots, full SHAs and workload SHA256 checksums. Every
revision must use identical workloads. The `pr1-base` harness is the actual post-PR122 merge-base
of PR1 (`a023336088c0df4fe887ee336dabcbbdb41f5b26`); `base` remains the cumulative f43854d comparison.
Do not substitute the cumulative baseline for the adjacent PR1 comparison. Source changes require rebuilding the relevant harness and a fresh evidence
directory. The revision list includes separate HTTP, .NET-10-only and constructor points before the final
SipHash runtime, preserving every adjacent comparison. Three independent launches per revision run in
descending/ascending/descending order. The twelve points give 72 family launches covering 36 revision rounds.
Each uses concurrent workstation GC, 16 warmups and 12 measured 500ms iterations.

The built harness accepts `--workload-inventory` followed by the exact timing filters. It uses
BenchmarkDotNet's converter, parameter expansion, glob filter and exported full case names without
running setup or timing. The script requires identical inventories and Program/workload checksums across
all revisions, then freezes the selected case names in the series manifest. With the current preliminary
GUID sources the inventory contains 47 local cases and 16 native cases; local compilation and generated
setup pass across all twelve revisions. Rebuild matching final sources and verify native setup on Nimbus
before freezing the timing cohort. Case counts come from the compiled inventory, not a
hard-coded total. Local groups run without test containers; native groups own fresh backends.
Every measured report must contain the exact selected names once, the expected report families and
twelve actual workload iterations per case. Duplicate, missing, substituted and warmup-only cases are
rejected; a zero host exit code alone does not establish successful benchmarks.

Builds/tests must finish before timing. A minute of observed quiet is required, and concurrent external
build/test activity makes the affected launch inadmissible. The process guard recognizes .NET, Flutter/Dart and
other common test/build runners, and excludes the benchmark's own compilation descendants. It is a useful check,
not proof against every possible source of system load. Other jobs are never terminated. Logs and JSON retain
all failed or interrupted attempts; a restart must preserve rejected attempt directories before reusing a run name.
The script only gathers evidence: section-10 acceptance still requires 95% intervals over the three independent run
means, allocation comparisons and review of uncertain/regressed cases. New RoundTrip/KeepCookies behavior is reported
separately where the historical feature was absent.

Run the guard regression tests with `python3 -m unittest discover -s benchmarks/MethodCache.Benchmarks/validation`.
The Flutter-worker case failed before the broadened guard; own benchmark descendants and normal backend/app
processes are checked separately. Interrupted evidence from the earlier .NET-only monitor is explicitly rejected.
Long-lived sleeping orphan test workers are not active jobs: the monitor checks their state, age, reported CPU and
CPU-time progress. An orphan using CPU is still detected even if its lifetime percentage rounds to zero. This
prevents stale workers from blocking forever without terminating them. The dormant-orphan collector regression
fails against the previous committed collector and passes with the fix; eight guard cases now cover these paths.

`create-prefix-harness.py --runtime-root CHECKOUT --workloads-root WORKLOAD_CHECKOUT --output OUTPUT` adds the separate
prefix-state/whole-span lookup and full contract-1 facade matrix: short/long prefixes, growing/large buffers,
sync/Task/ValueTask and MeterListener off/on. `MemoryCacheLookupReferenceBenchmarks` separates ready/built keys and
sync/Task result creation; those are different operations, not interchangeable baseline numbers. Post-PR5 statistics
have no separate toggle, so no ineffective statistics option is varied. Each benchmark setup has a correctness
contract test, and the macOS benchmark thread selects the same performance QoS on both revisions.
Build the external project and run its DLL with `--verify-components` before timing. This verifies
all prefix/buffer/listener combinations, repeated hits without origin loads, actual long-key growth,
lookup variants and all four memory-cache references. The harness explicitly references the same
DI implementation version as the benchmark tests: otherwise BDN's transitive DI 6 implementation
can be selected alongside newer abstractions and fail while constructing the provider. The original
failure and successful checks against both historical runtime points are retained in
`plans/design-benchmarks/component-harness-startup/`. No timing from the failed setup is accepted.
The hash-only component varies both Int and canonical GUID argument text at every prefix length;
this gives eighteen lookup cases, thirty-two facade/key-build cases and four references. Do not pool
the earlier int-only hash-component results with the expanded matrix.

## Cold HTTP publication versus growing-cache pressure

The original `HttpResponsePathBenchmarks.Miss` grows its resident set across iterations. The older
storage charges bodies only, while the new budget includes headers and metadata; that case therefore
also measures different eviction/GC pressure. Keep it as a diagnostic workload, not a pure publication
comparison. `HttpColdMissBenchmarks` resets its isolated provider outside each measured iteration and
uses an explicit 8,192 invocations with unroll factor 1. Every operation loads origin, and two complete
batches are checked to retain every response below the default budget, with no cross-batch residents.
A BenchmarkDotNet conversion test verifies that iteration setup does not reduce the batch to one call.

The validation harness selects fresh/variant/304 from HttpResponsePathBenchmarks and cold misses from
HttpColdMissBenchmarks. The compiled inventory records the selected cases and reports, while the
growing-miss variant remains a separate diagnostic. Growing local/native pressure in
DesignHttpWorkloadBenchmarks remains separately labelled.
Changed workloads require all baseline/candidate harnesses to be rebuilt with the exact same sources;
do not pool previous growing-miss data with this bounded publication case.

The explicit invocation count overrides iterationTime for this one class; other scenarios retain their
normal 500 ms target. See [BenchmarkDotNet InvocationCount](https://benchmarkdotnet.org/api/BenchmarkDotNet.Attributes.InvocationCountAttribute.html).
Growing-pressure rows still require performance review; the separation does not waive regressions.

## Independent-run statistical report

`summarize-pair.py RESULTS BASE CHANGE GROUP` defaults to three independent launches per revision.
`--rounds 6` or `--rounds 9` reads every launch in a fixed longer series, using df=5 or df=8 instead
of df=2 for the 95% Student-t interval over run means. Choose the longer count from a separate
baseline precision study, retain all launches, and complete the chosen series; do not stop or drop
runs when the result becomes favorable. These are independent processes, not iterations pooled
from one process. The [NIST Student-t table](https://www.itl.nist.gov/div898/handbook/eda/section3/eda3672.htm)
describes the two-sided 0.975 quantile.

An explicit `--method GenericHit` selects the exact method for a targeted supplementary series;
other cases are not assessed by that report. Every selected launch must still contain twelve actual
measured iterations and an empty interference guard. Base/change launch counts, case matrices and
workload checksums must match. Missing selected cases are errors, not empty success. Allocation
samples remain visible per run. The default full-series three-launch report is unchanged.

The tool identifies the specified disjoint-interval/>3% regression and labels relative interval
half-widths above 3% as insufficient precision for review. This diagnostic threshold does not modify
the specification or automatically accept narrower intervals. Every other row remains
`review_required`; feature equivalence, matching-hit allocation increases, pressure paths and the
complete scenario matrix still require assessment. Passing the helper's tests is not a performance pass.

## Affinity evidence

`bdn_affinity.py` parses BDN 0.15.4's declared affinity as binary at every display width;
`Affinity=100` means mask4. The job display alone does not establish the OS mask. See the
[versioned BDN implementation](https://github.com/dotnet/BenchmarkDotNet/blob/v0.15.4/src/BenchmarkDotNet/Extensions/ProcessExtensions.cs).
The Nimbus startup-affinity protocol uses taskset before .NET initializes, supplies no BDN
affinity characteristic, and samples the owned .NET process tree's actual CPU sets. Each of the
four executed child PIDs must have observations on CPU2; missing samples or different masks fail.
The V3 validation failure and fresh V4 protocol are retained under
`plans/design-benchmarks/independent-run-precision/startup-affinity-validation/`. Earlier samples
remain separate; all 31 affinity/activity/statistics helper tests pass, without a timing pass.
