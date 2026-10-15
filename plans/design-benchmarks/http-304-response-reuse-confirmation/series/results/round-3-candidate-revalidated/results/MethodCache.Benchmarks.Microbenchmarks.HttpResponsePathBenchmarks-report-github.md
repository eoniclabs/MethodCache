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
| **Revalidated** | **False**     | **False**             | **5.504 μs** | **0.0259 μs** | **0.0202 μs** | **0.8990** | **0.0110** |   **7.36 KB** |
| **Revalidated** | **False**     | **True**              | **5.494 μs** | **0.0213 μs** | **0.0154 μs** | **0.8985** | **0.0110** |   **7.36 KB** |
| **Revalidated** | **True**      | **False**             | **5.717 μs** | **0.0118 μs** | **0.0085 μs** | **0.9168** | **0.0115** |   **7.55 KB** |
| **Revalidated** | **True**      | **True**              | **5.710 μs** | **0.0326 μs** | **0.0254 μs** | **0.9233** | **0.0113** |   **7.55 KB** |
