```

BenchmarkDotNet v0.15.4, Linux Ubuntu 24.04.5 LTS (Noble Numbat) (container)
11th Gen Intel Core i9-11900 2.50GHz (Max: 0.80GHz), 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-ACOERQ : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Concurrent=True  Server=False  IterationCount=12
IterationTime=500ms  LaunchCount=1  WarmupCount=16

```
| Method      | Listening | AfterInvalidation | Mean     | Error     | StdDev    | Gen0   | Gen1   | Allocated |
|------------ |---------- |------------------ |---------:|----------:|----------:|-------:|-------:|----------:|
| **Revalidated** | **False**     | **False**             | **5.322 μs** | **0.0376 μs** | **0.0293 μs** | **0.9825** | **0.0106** |   **8.06 KB** |
| **Revalidated** | **False**     | **True**              | **5.293 μs** | **0.0649 μs** | **0.0506 μs** | **0.9860** | **0.0106** |   **8.06 KB** |
| **Revalidated** | **True**      | **False**             | **5.132 μs** | **0.0158 μs** | **0.0114 μs** | **0.9843** | **0.0103** |   **8.06 KB** |
| **Revalidated** | **True**      | **True**              | **5.212 μs** | **0.0174 μs** | **0.0126 μs** | **0.9782** | **0.0104** |   **8.06 KB** |
