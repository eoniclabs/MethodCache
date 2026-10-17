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
| SyncHit      | 38.99 ns | 0.154 ns | 0.092 ns |         - |
| GenericHit   | 55.07 ns | 0.485 ns | 0.350 ns |         - |
| TaskHit      | 41.59 ns | 1.468 ns | 1.062 ns |         - |
| ValueTaskHit | 46.76 ns | 1.030 ns | 0.745 ns |         - |
