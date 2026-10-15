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
| **Revalidated** | **False**     | **False**             | **5.280 μs** | **0.0112 μs** | **0.0081 μs** | **0.9799** | **0.0105** |   **8.06 KB** |
| **Revalidated** | **False**     | **True**              | **5.079 μs** | **0.0390 μs** | **0.0305 μs** | **0.9800** | **0.0101** |   **8.06 KB** |
| **Revalidated** | **True**      | **False**             | **5.145 μs** | **0.0554 μs** | **0.0400 μs** | **0.9789** | **0.0101** |   **8.06 KB** |
| **Revalidated** | **True**      | **True**              | **5.160 μs** | **0.0260 μs** | **0.0188 μs** | **0.9829** | **0.0101** |   **8.06 KB** |
