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
| **Revalidated** | **False**     | **False**             | **5.296 μs** | **0.0177 μs** | **0.0138 μs** | **0.8978** | **0.0106** |   **7.36 KB** |
| **Revalidated** | **False**     | **True**              | **5.584 μs** | **0.0232 μs** | **0.0153 μs** | **0.8933** | **0.0112** |   **7.36 KB** |
| **Revalidated** | **True**      | **False**             | **5.643 μs** | **0.0367 μs** | **0.0287 μs** | **0.9160** | **0.0112** |   **7.55 KB** |
| **Revalidated** | **True**      | **True**              | **5.753 μs** | **0.0279 μs** | **0.0202 μs** | **0.9166** | **0.0115** |   **7.55 KB** |
