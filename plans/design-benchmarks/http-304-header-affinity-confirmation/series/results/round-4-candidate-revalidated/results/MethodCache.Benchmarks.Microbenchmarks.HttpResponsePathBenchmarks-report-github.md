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
| **Revalidated** | **False**     | **False**             |  **7.926 μs** | **0.1639 μs** | **0.1280 μs** | **0.8333** |   **7.03 KB** |
| **Revalidated** | **False**     | **True**              | **17.694 μs** | **7.7692 μs** | **6.0657 μs** | **0.8256** |      **7 KB** |
| **Revalidated** | **True**      | **False**             | **17.338 μs** | **6.0741 μs** | **4.3920 μs** | **0.8633** |   **7.23 KB** |
| **Revalidated** | **True**      | **True**              | **11.560 μs** | **0.0308 μs** | **0.0204 μs** | **0.8657** |   **7.28 KB** |
