```

BenchmarkDotNet v0.15.4, Linux Ubuntu 24.04.5 LTS (Noble Numbat) (container)
11th Gen Intel Core i9-11900 2.50GHz (Max: 0.80GHz), 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-ACOERQ : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Concurrent=True  Server=False  IterationCount=12
IterationTime=500ms  LaunchCount=1  WarmupCount=16

```
| Method      | Listening | AfterInvalidation | Mean      | Error     | StdDev    | Gen0   | Allocated |
|------------ |---------- |------------------ |----------:|----------:|----------:|-------:|----------:|
| **Revalidated** | **False**     | **False**             |  **7.309 μs** | **0.0831 μs** | **0.0601 μs** | **0.8333** |   **7.03 KB** |
| **Revalidated** | **False**     | **True**              | **13.416 μs** | **2.6754 μs** | **1.7696 μs** | **0.8344** |      **7 KB** |
| **Revalidated** | **True**      | **False**             | **20.075 μs** | **4.7242 μs** | **3.6884 μs** | **0.8527** |   **7.23 KB** |
| **Revalidated** | **True**      | **True**              |  **8.111 μs** | **0.0334 μs** | **0.0221 μs** | **0.8714** |   **7.23 KB** |
