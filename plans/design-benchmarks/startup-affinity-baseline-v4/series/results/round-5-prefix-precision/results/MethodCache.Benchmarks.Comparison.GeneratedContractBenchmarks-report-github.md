```

BenchmarkDotNet v0.15.4, Linux Ubuntu 24.04.5 LTS (Noble Numbat) (container)
11th Gen Intel Core i9-11900 2.50GHz (Max: 0.80GHz), 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-ACOERQ : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Concurrent=True  Server=False  IterationCount=12
IterationTime=500ms  LaunchCount=1  WarmupCount=16

```
| Method       | Mean     | Error    | StdDev   | Allocated |
|------------- |---------:|---------:|---------:|----------:|
| SyncHit      | 38.56 ns | 0.295 ns | 0.214 ns |         - |
| GenericHit   | 53.05 ns | 0.896 ns | 0.648 ns |         - |
| TaskHit      | 40.38 ns | 0.313 ns | 0.245 ns |         - |
| ValueTaskHit | 47.29 ns | 0.653 ns | 0.510 ns |         - |
