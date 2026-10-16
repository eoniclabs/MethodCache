```

BenchmarkDotNet v0.15.4, Linux Ubuntu 24.04.5 LTS (Noble Numbat) (container)
11th Gen Intel Core i9-11900 2.50GHz (Max: 0.80GHz), 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-ACOERQ : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Concurrent=True  Server=False  IterationCount=12
IterationTime=500ms  LaunchCount=1  WarmupCount=16

```
| Method      | Listening | AfterInvalidation | Mean     | Error     | StdDev   | Gen0   | Allocated |
|------------ |---------- |------------------ |---------:|----------:|---------:|-------:|----------:|
| **Revalidated** | **False**     | **False**             | **18.38 μs** |  **0.174 μs** | **0.126 μs** | **0.9817** |   **8.15 KB** |
| **Revalidated** | **False**     | **True**              | **20.97 μs** |  **2.897 μs** | **2.262 μs** | **0.9778** |   **8.12 KB** |
| **Revalidated** | **True**      | **False**             | **13.48 μs** | **10.133 μs** | **7.912 μs** | **0.9448** |   **8.06 KB** |
| **Revalidated** | **True**      | **True**              | **20.43 μs** |  **6.701 μs** | **5.232 μs** | **0.9130** |   **8.06 KB** |
