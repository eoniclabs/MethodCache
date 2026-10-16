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
| SyncHit      | 38.93 ns | 0.141 ns | 0.102 ns |         - |
| GenericHit   | 62.44 ns | 3.123 ns | 2.258 ns |         - |
| TaskHit      | 40.16 ns | 0.184 ns | 0.110 ns |         - |
| ValueTaskHit | 45.12 ns | 0.358 ns | 0.237 ns |         - |
