```

BenchmarkDotNet v0.15.4, Linux Ubuntu 24.04.5 LTS (Noble Numbat) (container)
11th Gen Intel Core i9-11900 2.50GHz (Max: 0.80GHz), 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-ACOERQ : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Concurrent=True  Server=False  IterationCount=12
IterationTime=500ms  LaunchCount=1  WarmupCount=16

```
| Method      | Listening | AfterInvalidation | Mean     | Error    | StdDev   | Gen0   | Allocated |
|------------ |---------- |------------------ |---------:|---------:|---------:|-------:|----------:|
| **Revalidated** | **False**     | **False**             | **17.14 μs** | **3.065 μs** | **2.216 μs** | **0.9070** |   **8.06 KB** |
| **Revalidated** | **False**     | **True**              | **20.33 μs** | **0.599 μs** | **0.468 μs** | **0.9894** |   **8.15 KB** |
| **Revalidated** | **True**      | **False**             | **20.55 μs** | **2.370 μs** | **1.567 μs** | **0.9690** |   **8.06 KB** |
| **Revalidated** | **True**      | **True**              | **17.22 μs** | **7.627 μs** | **5.955 μs** | **0.9458** |   **8.06 KB** |
