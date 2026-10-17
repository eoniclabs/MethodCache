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
| **Revalidated** | **False**     | **False**             | **21.85 μs** | **4.793 μs** | **3.742 μs** | **0.9203** |   **8.06 KB** |
| **Revalidated** | **False**     | **True**              | **22.77 μs** | **2.791 μs** | **2.179 μs** | **0.9203** |   **8.15 KB** |
| **Revalidated** | **True**      | **False**             | **21.27 μs** | **3.411 μs** | **2.663 μs** | **0.9830** |   **8.12 KB** |
| **Revalidated** | **True**      | **True**              | **17.28 μs** | **7.470 μs** | **5.832 μs** | **0.9271** |   **8.06 KB** |
