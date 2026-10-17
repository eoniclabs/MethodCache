```

BenchmarkDotNet v0.15.4, Linux Ubuntu 24.04.5 LTS (Noble Numbat) (container)
11th Gen Intel Core i9-11900 2.50GHz (Max: 0.80GHz), 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-ACOERQ : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Concurrent=True  Server=False  IterationCount=12
IterationTime=500ms  LaunchCount=1  WarmupCount=16

```
| Method      | Listening | AfterInvalidation | Mean     | Error     | StdDev   | Median   | Gen0   | Allocated |
|------------ |---------- |------------------ |---------:|----------:|---------:|---------:|-------:|----------:|
| **Revalidated** | **False**     | **False**             | **16.29 μs** |  **3.710 μs** | **2.683 μs** | **14.09 μs** | **0.9894** |   **8.12 KB** |
| **Revalidated** | **False**     | **True**              | **15.10 μs** |  **8.925 μs** | **6.968 μs** | **19.51 μs** | **0.9506** |   **8.06 KB** |
| **Revalidated** | **True**      | **False**             | **14.18 μs** | **10.387 μs** | **8.110 μs** | **13.87 μs** | **0.9855** |   **8.06 KB** |
| **Revalidated** | **True**      | **True**              | **13.39 μs** |  **9.852 μs** | **7.692 μs** | **12.48 μs** | **0.9459** |   **8.06 KB** |
