```

BenchmarkDotNet v0.15.4, Linux Ubuntu 24.04.5 LTS (Noble Numbat) (container)
11th Gen Intel Core i9-11900 2.50GHz (Max: 0.80GHz), 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-ACOERQ : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Concurrent=True  Server=False  IterationCount=12
IterationTime=500ms  LaunchCount=1  WarmupCount=16

```
| Method      | Listening | AfterInvalidation | Mean     | Error     | StdDev    | Gen0   | Gen1   | Allocated |
|------------ |---------- |------------------ |---------:|----------:|----------:|-------:|-------:|----------:|
| **Revalidated** | **False**     | **False**             | **5.178 μs** | **0.0203 μs** | **0.0158 μs** | **0.9817** | **0.0103** |   **8.06 KB** |
| **Revalidated** | **False**     | **True**              | **5.288 μs** | **0.0364 μs** | **0.0284 μs** | **0.9864** | **0.0105** |   **8.06 KB** |
| **Revalidated** | **True**      | **False**             | **5.029 μs** | **0.0153 μs** | **0.0111 μs** | **0.9816** | **0.0101** |   **8.06 KB** |
| **Revalidated** | **True**      | **True**              | **4.972 μs** | **0.0147 μs** | **0.0106 μs** | **0.9789** | **0.0099** |   **8.06 KB** |
