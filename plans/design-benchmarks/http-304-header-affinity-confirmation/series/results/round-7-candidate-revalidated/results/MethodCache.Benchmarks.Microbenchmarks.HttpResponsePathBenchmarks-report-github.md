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
| **Revalidated** | **False**     | **False**             | **14.28 μs** | **4.986 μs** | **3.893 μs** | **0.8344** |   **7.03 KB** |
| **Revalidated** | **False**     | **True**              | **17.94 μs** | **8.398 μs** | **6.557 μs** | **0.8300** |      **7 KB** |
| **Revalidated** | **True**      | **False**             | **17.41 μs** | **7.060 μs** | **5.105 μs** | **0.8754** |   **7.23 KB** |
| **Revalidated** | **True**      | **True**              | **12.98 μs** | **0.110 μs** | **0.073 μs** | **0.8693** |    **7.2 KB** |
