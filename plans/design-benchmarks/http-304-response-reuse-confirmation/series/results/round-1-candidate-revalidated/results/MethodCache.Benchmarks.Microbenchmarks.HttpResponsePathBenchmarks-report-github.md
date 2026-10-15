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
| **Revalidated** | **False**     | **False**             | **5.653 μs** | **0.0225 μs** | **0.0163 μs** | **0.8958** | **0.0113** |   **7.36 KB** |
| **Revalidated** | **False**     | **True**              | **5.597 μs** | **0.0262 μs** | **0.0205 μs** | **0.8911** | **0.0111** |   **7.36 KB** |
| **Revalidated** | **True**      | **False**             | **5.645 μs** | **0.0204 μs** | **0.0147 μs** | **0.9145** | **0.0113** |   **7.55 KB** |
| **Revalidated** | **True**      | **True**              | **5.845 μs** | **0.0162 μs** | **0.0117 μs** | **0.9222** | **0.0117** |   **7.55 KB** |
