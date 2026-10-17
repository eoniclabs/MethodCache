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
| **Revalidated** | **False**     | **False**             | **19.80 μs** | **10.098 μs** | **7.883 μs** | **24.44 μs** | **0.8492** |      **7 KB** |
| **Revalidated** | **False**     | **True**              | **16.48 μs** |  **8.735 μs** | **6.819 μs** | **20.28 μs** | **0.8256** |      **7 KB** |
| **Revalidated** | **True**      | **False**             | **17.29 μs** | **10.218 μs** | **7.977 μs** | **21.82 μs** | **0.8645** |    **7.2 KB** |
| **Revalidated** | **True**      | **True**              | **14.58 μs** |  **6.533 μs** | **5.100 μs** | **15.97 μs** | **0.8527** |   **7.23 KB** |
