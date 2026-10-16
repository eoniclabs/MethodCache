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
| **Revalidated** | **False**     | **False**             | **5.222 μs** | **0.0114 μs** | **0.0089 μs** | **0.9792** | **0.0104** |   **8.06 KB** |
| **Revalidated** | **False**     | **True**              | **5.492 μs** | **0.0198 μs** | **0.0155 μs** | **0.9846** | **0.0109** |   **8.06 KB** |
| **Revalidated** | **True**      | **False**             | **5.227 μs** | **0.0154 μs** | **0.0111 μs** | **0.9821** | **0.0104** |   **8.06 KB** |
| **Revalidated** | **True**      | **True**              | **5.385 μs** | **0.0232 μs** | **0.0168 μs** | **0.9804** | **0.0108** |   **8.06 KB** |
