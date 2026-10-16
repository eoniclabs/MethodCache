```

BenchmarkDotNet v0.15.4, Linux Ubuntu 24.04.5 LTS (Noble Numbat) (container)
11th Gen Intel Core i9-11900 2.50GHz (Max: 0.80GHz), 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-ACOERQ : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Concurrent=True  Server=False  IterationCount=12
IterationTime=500ms  LaunchCount=1  WarmupCount=16

```
| Method      | Listening | AfterInvalidation | Mean     | Error    | StdDev   | Gen0   | Allocated |
|------------ |---------- |------------------ |---------:|---------:|---------:|-------:|----------:|
| **Revalidated** | **False**     | **False**             | **15.59 μs** | **2.703 μs** | **1.788 μs** | **0.9278** |   **8.06 KB** |
| **Revalidated** | **False**     | **True**              | **11.69 μs** | **0.050 μs** | **0.036 μs** | **0.9855** |   **8.12 KB** |
| **Revalidated** | **True**      | **False**             | **14.41 μs** | **9.880 μs** | **7.713 μs** | **0.9653** |   **8.06 KB** |
| **Revalidated** | **True**      | **True**              | **17.29 μs** | **7.623 μs** | **5.952 μs** | **0.9766** |   **8.06 KB** |
