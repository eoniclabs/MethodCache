```

BenchmarkDotNet v0.15.4, Linux Ubuntu 24.04.5 LTS (Noble Numbat) (container)
11th Gen Intel Core i9-11900 2.50GHz (Max: 0.80GHz), 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-ACOERQ : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Concurrent=True  Server=False  IterationCount=12
IterationTime=500ms  LaunchCount=1  WarmupCount=16

```
| Method      | Listening | AfterInvalidation | Mean     | Error    | StdDev   | Median   | Gen0   | Allocated |
|------------ |---------- |------------------ |---------:|---------:|---------:|---------:|-------:|----------:|
| **Revalidated** | **False**     | **False**             | **18.19 μs** | **6.015 μs** | **4.349 μs** | **20.24 μs** | **0.9058** |   **8.06 KB** |
| **Revalidated** | **False**     | **True**              | **14.42 μs** | **9.594 μs** | **7.491 μs** | **16.68 μs** | **0.9740** |   **8.06 KB** |
| **Revalidated** | **True**      | **False**             | **16.42 μs** | **8.235 μs** | **6.429 μs** | **20.00 μs** | **0.9690** |   **8.06 KB** |
| **Revalidated** | **True**      | **True**              | **18.31 μs** | **6.618 μs** | **5.167 μs** | **20.59 μs** | **0.9615** |   **8.06 KB** |
