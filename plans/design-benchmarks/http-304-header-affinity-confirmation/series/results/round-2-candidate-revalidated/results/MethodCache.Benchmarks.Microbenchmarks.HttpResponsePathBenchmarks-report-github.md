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
| **Revalidated** | **False**     | **False**             | **14.18 μs** |  **9.095 μs** | **7.101 μs** | **0.8367** |      **7 KB** |
| **Revalidated** | **False**     | **True**              | **14.01 μs** |  **4.726 μs** | **3.417 μs** | **0.8300** |      **7 KB** |
| **Revalidated** | **True**      | **False**             | **14.49 μs** | **10.145 μs** | **7.921 μs** | **0.8633** |    **7.2 KB** |
| **Revalidated** | **True**      | **True**              | **19.36 μs** |  **4.578 μs** | **3.574 μs** | **0.8127** |   **7.23 KB** |
