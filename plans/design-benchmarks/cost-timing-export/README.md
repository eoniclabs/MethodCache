# Cost-profile timing export

BenchmarkDotNet 0.15.4 already normalizes `BenchmarkReport.ResultStatistics` per operation. Dividing its median by `OperationsPerInvoke` again exports incorrect units for batched workloads. The original exporter also accepted reports with fewer than the configured twelve actual workload iterations.

The original dictionary was extracted unchanged into an internal helper and compiled in the benchmark tests. Synthetic BenchmarkDotNet reports use its real Measurement/ExecuteResult/statistics parser: 1, 16 and 64 operations per invocation each cost 1000 ns per operation. Before correction the batched exports are 62.5 and 15.62 ns, and incomplete reports export a time. Four of five tests fail in `red.log`. After correction the full 120-test benchmark project passes (`green.log`). The exporter retains the actual iterations and runtime/GC metadata; the runtime library and measured workloads are unchanged.

Primary implementation: [BenchmarkReport.ResultStatistics](https://github.com/dotnet/BenchmarkDotNet/blob/v0.15.4/src/BenchmarkDotNet/Reports/BenchmarkReport.cs) uses [Measurement.GetAverageTime](https://github.com/dotnet/BenchmarkDotNet/blob/v0.15.4/src/BenchmarkDotNet/Reports/Measurement.cs), which divides total nanoseconds by operation count.

The manifest records the red setup, exact source hashes and original/stored log hashes. This is exporter correctness evidence, not performance acceptance.
