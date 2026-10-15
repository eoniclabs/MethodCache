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
| **Revalidated** | **False**     | **False**             | **5.276 μs** | **0.0178 μs** | **0.0139 μs** | **0.9784** | **0.0105** |   **8.06 KB** |
| **Revalidated** | **False**     | **True**              | **4.992 μs** | **0.0110 μs** | **0.0080 μs** | **0.9859** | **0.0100** |   **8.06 KB** |
| **Revalidated** | **True**      | **False**             | **5.129 μs** | **0.0246 μs** | **0.0192 μs** | **0.9818** | **0.0102** |   **8.06 KB** |
| **Revalidated** | **True**      | **True**              | **5.306 μs** | **0.0465 μs** | **0.0363 μs** | **0.9836** | **0.0105** |   **8.06 KB** |
