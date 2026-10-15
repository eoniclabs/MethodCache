# Design-series performance evidence

`create-harness.py --runtime-root CHECKOUT --output OUTPUT` copies identical public workload sources into an
external BenchmarkDotNet project. Build that project in Release, then invoke its DLL with `--verify-native` before
timing. Real Redis/SQL connection variables are required; setup checks the HTTP storage mode, repeated real L2 reads,
origin loads and rejection of L1 admission. Older HTTP revisions explicitly enable their separate L2 option.

`create-native-driver.py --output OUTPUT` creates an external test-container runner. Build `Runner.csproj`, then run
its DLL with a working directory followed by the child executable and arguments. It supplies fresh Redis 7 and SQL
Server 2022 containers and credentials through the child's environment, checks its exit code and disposes its own
containers. It does not print credentials or touch a production backend.

`run-series.py` expects the eight prepared harnesses under `/tmp/methodcache-design-harness` and the built driver at
`/tmp/methodcache-http-native-runner/bin/Release/net10.0/Runner.dll`. These paths are configurable as constants at the
top of the script. Each manifest records absolute runtime roots, full SHAs and workload SHA256 checksums. Every
revision must use identical workloads. Source changes require rebuilding the relevant harness and a fresh evidence
directory. The script runs 62 cases in three independent launches per revision, descending/ascending/descending,
so adjacent base/change order alternates. Every launch owns fresh native backends, uses concurrent workstation GC,
16 warmups and 12 measured 500ms iterations. It checks every report for measured results; a zero host exit code alone
does not establish successful benchmarks.

Builds/tests must finish before timing. A minute of observed quiet is required, and concurrent external
build/test activity makes the affected launch inadmissible. Other jobs are never terminated. Logs and JSON retain
all failed or interrupted attempts; a restart must preserve rejected attempt directories before reusing a run name.
The script only gathers evidence: section-10 acceptance still requires 95% intervals over the three independent run
means, allocation comparisons and review of uncertain/regressed cases. New RoundTrip/KeepCookies behavior is reported
separately where the historical feature was absent.

`create-prefix-harness.py --runtime-root CHECKOUT --workloads-root WORKLOAD_CHECKOUT --output OUTPUT` adds the separate
prefix-state/whole-span lookup and full contract-1 facade matrix: short/long prefixes, growing/large buffers,
sync/Task/ValueTask and MeterListener off/on. `MemoryCacheLookupReferenceBenchmarks` separates ready/built keys and
sync/Task result creation; those are different operations, not interchangeable baseline numbers. Post-PR5 statistics
have no separate toggle, so no ineffective statistics option is varied. Each benchmark setup has a correctness
contract test, and the macOS benchmark thread selects the same performance QoS on both revisions.
