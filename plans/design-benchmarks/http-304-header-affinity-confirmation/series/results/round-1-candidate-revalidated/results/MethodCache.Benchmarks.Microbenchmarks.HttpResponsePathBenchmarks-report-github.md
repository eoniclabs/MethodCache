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
| **Revalidated** | **False**     | **False**             | **14.95 μs** | **6.450 μs** | **5.036 μs** | **17.31 μs** | **0.8435** |      **7 KB** |
| **Revalidated** | **False**     | **True**              | **13.26 μs** | **2.005 μs** | **1.450 μs** | **13.77 μs** | **0.8300** |      **7 KB** |
| **Revalidated** | **True**      | **False**             | **18.56 μs** | **9.540 μs** | **7.448 μs** | **23.08 μs** | **0.8117** |   **7.23 KB** |
| **Revalidated** | **True**      | **True**              | **18.48 μs** | **9.579 μs** | **7.478 μs** | **22.79 μs** | **0.8085** |    **7.2 KB** |
