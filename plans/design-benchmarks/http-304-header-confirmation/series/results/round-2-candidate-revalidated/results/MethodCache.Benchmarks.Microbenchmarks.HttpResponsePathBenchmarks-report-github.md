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
| **Revalidated** | **False**     | **False**             | **5.291 μs** | **0.0134 μs** | **0.0089 μs** | **0.8486** | **0.0106** |      **7 KB** |
| **Revalidated** | **False**     | **True**              | **5.370 μs** | **0.0137 μs** | **0.0107 μs** | **0.8568** | **0.0107** |      **7 KB** |
| **Revalidated** | **True**      | **False**             | **5.493 μs** | **0.0145 μs** | **0.0096 μs** | **0.8789** | **0.0110** |    **7.2 KB** |
| **Revalidated** | **True**      | **True**              | **5.454 μs** | **0.0383 μs** | **0.0277 μs** | **0.8768** | **0.0108** |    **7.2 KB** |
