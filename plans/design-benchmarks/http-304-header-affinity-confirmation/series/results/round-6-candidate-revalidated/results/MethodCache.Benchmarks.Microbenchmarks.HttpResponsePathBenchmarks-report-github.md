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
| **Revalidated** | **False**     | **False**             | **17.96 μs** | **9.379 μs** | **7.323 μs** | **22.23 μs** | **0.8467** |      **7 KB** |
| **Revalidated** | **False**     | **True**              | **13.67 μs** | **5.740 μs** | **4.482 μs** | **15.56 μs** | **0.8333** |      **7 KB** |
| **Revalidated** | **True**      | **False**             | **16.98 μs** | **7.927 μs** | **5.243 μs** | **19.99 μs** | **0.8550** |   **7.23 KB** |
| **Revalidated** | **True**      | **True**              | **14.34 μs** | **9.538 μs** | **7.446 μs** | **15.92 μs** | **0.8597** |    **7.2 KB** |
