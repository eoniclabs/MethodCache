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
| **Revalidated** | **False**     | **False**             | **5.339 μs** | **0.0546 μs** | **0.0394 μs** | **0.8493** | **0.0106** |      **7 KB** |
| **Revalidated** | **False**     | **True**              | **5.385 μs** | **0.0155 μs** | **0.0121 μs** | **0.8478** | **0.0107** |      **7 KB** |
| **Revalidated** | **True**      | **False**             | **5.388 μs** | **0.0185 μs** | **0.0144 μs** | **0.8727** | **0.0108** |    **7.2 KB** |
| **Revalidated** | **True**      | **True**              | **5.801 μs** | **0.0276 μs** | **0.0200 μs** | **0.8764** | **0.0115** |    **7.2 KB** |
