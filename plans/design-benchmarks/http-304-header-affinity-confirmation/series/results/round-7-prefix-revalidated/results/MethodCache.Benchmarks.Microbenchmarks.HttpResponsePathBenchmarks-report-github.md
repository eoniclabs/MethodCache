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
| **Revalidated** | **False**     | **False**             | **14.48 μs** | **6.290 μs** | **4.548 μs** | **16.86 μs** | **0.9791** |   **8.06 KB** |
| **Revalidated** | **False**     | **True**              | **14.91 μs** | **9.260 μs** | **7.230 μs** | **18.63 μs** | **0.9696** |   **8.06 KB** |
| **Revalidated** | **True**      | **False**             | **20.35 μs** | **0.259 μs** | **0.187 μs** | **20.32 μs** | **0.9591** |   **8.15 KB** |
| **Revalidated** | **True**      | **True**              | **17.36 μs** | **7.751 μs** | **5.604 μs** | **20.34 μs** | **0.9566** |   **8.06 KB** |
