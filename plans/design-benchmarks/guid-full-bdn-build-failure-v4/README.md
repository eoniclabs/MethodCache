# V4 timing attempt: generated program fails before execution

The first full-series batch acquires its exclusive lease at 08:30:54 UTC on 2026-10-16,
then exits 1 at 08:32:09. No benchmark executes and no timing launch succeeds. The complete
attempt remains on Nimbus under `guid-final-v4/timing-results`; selected original driver,
worker, BDN logs and generated-source excerpts are retained here with original/stored hashes.
The matching prepared manifest and frozen protocol are in `guid-full-preparation-v4`.

BDN's generated C# assigns `HttpColdMissBenchmarks.ResetBatch` to its synchronous iteration
setup delegate. The method returns Task, producing four CS0407 errors. The harness itself
compiles successfully, and compiled-inventory/setup guards had passed; those do not compile
BDN's generated executable. BDN's host returns zero despite this generation/build failure;
the worker correctly rejects the missing executions, although its GC diagnostic is secondary.
Neither the guard failure nor this rejected attempt supplies a timing result.

The regression test binds all benchmark iteration hooks to the Action used by generated
programs. It fails before the correction with the incompatible-signature error. All 113
benchmark tests pass afterward, including origin/resident-state checks for two reset batches
in all four listener/invalidation combinations. ResetBatch is now a void boundary that waits
for the existing asynchronous reset outside the measured operation; GlobalSetup remains
asynchronous. The measured cache call, batch size, setup semantics and case set are unchanged.

A new `--build-only '*'` branch uses BDN 0.15.4's real partitioner, generator and builder and
never executes benchmarks. With the old Task hook, the harness compiles but this generated
build fails CS0407; the corrected hook compiles. Every primary and component runtime must
pass that generated-program gate before a fresh fixed timing cohort. V4 is retained unchanged;
no successful samples exist to carry forward or pool.

The same generated-build gate also finds a separate component-harness naming error:
`PrefixValidation.csproj` emits `MethodCache.Benchmarks.dll`. BDN cannot discover that project
by the assembly name, so generation fails even though the harness itself builds. A factory
regression test fails before the project is renamed `MethodCache.Benchmarks.csproj`; the
assembly name (needed by the existing runtime friend access) is unchanged. The corrected
primary generated program compiles all 67 cases (including four untimed growing-cache diagnostics),
and the corrected component generated program compiles all 102 cases, with no benchmark
execution. All 53 Python validation tests pass. Complete red/green logs are retained here.
