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
| **Revalidated** | **False**     | **False**             | **17.18 μs** | **5.059 μs** | **3.658 μs** | **19.06 μs** | **0.9542** |   **8.06 KB** |
| **Revalidated** | **False**     | **True**              | **15.01 μs** | **9.400 μs** | **7.339 μs** | **18.93 μs** | **0.9777** |   **8.06 KB** |
| **Revalidated** | **True**      | **False**             | **17.74 μs** | **6.869 μs** | **4.967 μs** | **20.20 μs** | **0.9070** |   **8.06 KB** |
| **Revalidated** | **True**      | **True**              | **21.69 μs** | **2.413 μs** | **1.884 μs** | **22.27 μs** | **0.9640** |   **8.12 KB** |
