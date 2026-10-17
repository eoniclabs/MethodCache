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
| **Revalidated** | **False**     | **False**             | **5.432 μs** | **0.0149 μs** | **0.0099 μs** | **0.8481** | **0.0109** |      **7 KB** |
| **Revalidated** | **False**     | **True**              | **5.460 μs** | **0.0094 μs** | **0.0073 μs** | **0.8499** | **0.0109** |      **7 KB** |
| **Revalidated** | **True**      | **False**             | **5.742 μs** | **0.0469 μs** | **0.0366 μs** | **0.8756** | **0.0114** |    **7.2 KB** |
| **Revalidated** | **True**      | **True**              | **5.429 μs** | **0.0096 μs** | **0.0075 μs** | **0.8798** | **0.0109** |    **7.2 KB** |
