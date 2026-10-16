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
| **Revalidated** | **False**     | **False**             | **12.78 μs** | **0.939 μs** | **0.559 μs** | **12.96 μs** | **0.8245** |      **7 KB** |
| **Revalidated** | **False**     | **True**              | **17.64 μs** | **8.668 μs** | **6.768 μs** | **21.50 μs** | **0.8509** |      **7 KB** |
| **Revalidated** | **True**      | **False**             | **18.15 μs** | **8.170 μs** | **6.379 μs** | **21.03 μs** | **0.8597** |    **7.2 KB** |
| **Revalidated** | **True**      | **True**              | **13.87 μs** | **5.619 μs** | **4.387 μs** | **16.24 μs** | **0.8597** |   **7.23 KB** |
