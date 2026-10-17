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
| **Revalidated** | **False**     | **False**             | **14.25 μs** | **9.429 μs** | **7.362 μs** | **16.72 μs** | **0.9593** |   **8.06 KB** |
| **Revalidated** | **False**     | **True**              | **19.19 μs** | **4.709 μs** | **3.115 μs** | **20.43 μs** | **0.9830** |   **8.06 KB** |
| **Revalidated** | **True**      | **False**             | **16.65 μs** | **7.542 μs** | **5.888 μs** | **19.80 μs** | **0.9728** |   **8.06 KB** |
| **Revalidated** | **True**      | **True**              | **17.48 μs** | **8.711 μs** | **6.801 μs** | **21.40 μs** | **0.9494** |   **8.06 KB** |
