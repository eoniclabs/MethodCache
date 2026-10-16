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
| **Revalidated** | **False**     | **False**             | **12.881 μs** | **0.8626 μs** | **0.5706 μs** | **0.8356** |      **7 KB** |
| **Revalidated** | **False**     | **True**              |  **7.061 μs** | **0.0430 μs** | **0.0284 μs** | **0.8364** |      **7 KB** |
| **Revalidated** | **True**      | **False**             | **13.952 μs** | **8.7454 μs** | **6.8278 μs** | **0.8127** |    **7.2 KB** |
| **Revalidated** | **True**      | **True**              | **15.058 μs** | **0.1049 μs** | **0.0694 μs** | **0.8693** |   **7.23 KB** |
