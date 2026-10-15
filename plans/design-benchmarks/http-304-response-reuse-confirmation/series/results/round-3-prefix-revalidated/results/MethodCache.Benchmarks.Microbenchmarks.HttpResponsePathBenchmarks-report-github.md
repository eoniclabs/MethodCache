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
| **Revalidated** | **False**     | **False**             | **5.148 μs** | **0.0123 μs** | **0.0089 μs** | **0.9824** | **0.0103** |   **8.06 KB** |
| **Revalidated** | **False**     | **True**              | **5.264 μs** | **0.0352 μs** | **0.0275 μs** | **0.9818** | **0.0104** |   **8.06 KB** |
| **Revalidated** | **True**      | **False**             | **5.386 μs** | **0.0326 μs** | **0.0236 μs** | **0.9811** | **0.0108** |   **8.06 KB** |
| **Revalidated** | **True**      | **True**              | **5.360 μs** | **0.0207 μs** | **0.0161 μs** | **0.9801** | **0.0107** |   **8.06 KB** |
