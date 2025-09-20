```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6.1 (24G90) [Darwin 24.6.0]
Apple M2, 1 CPU, 8 logical and 8 physical cores
.NET SDK 9.0.305
  [Host]     : .NET 9.0.9 (9.0.925.41916), Arm64 RyuJIT AdvSIMD
  DefaultJob : .NET 9.0.9 (9.0.925.41916), Arm64 RyuJIT AdvSIMD

Concurrent=True  Server=True  

```
| Method            | Job        | Platform | RetainVm | Toolchain              | DataSize | ModelType | Mean | Error | Ratio | RatioSD | Rank | Alloc Ratio |
|------------------ |----------- |--------- |--------- |----------------------- |--------- |---------- |-----:|------:|------:|--------:|-----:|------------:|
| **NoCaching**         | **DefaultJob** | **Arm64**    | **False**    | **Default**                | **1**        | **Large**     |   **NA** |    **NA** |     **?** |       **?** |    **?** |           **?** |
| CacheMiss         | DefaultJob | Arm64    | False    | Default                | 1        | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | DefaultJob | Arm64    | False    | Default                | 1        | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | DefaultJob | Arm64    | False    | Default                | 1        | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | DefaultJob | Arm64    | False    | Default                | 1        | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | DefaultJob | Arm64    | False    | Default                | 1        | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| NoCaching         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1        | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheMiss         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1        | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1        | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1        | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1        | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1        | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| **NoCaching**         | **DefaultJob** | **Arm64**    | **False**    | **Default**                | **1**        | **Medium**    |   **NA** |    **NA** |     **?** |       **?** |    **?** |           **?** |
| CacheMiss         | DefaultJob | Arm64    | False    | Default                | 1        | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | DefaultJob | Arm64    | False    | Default                | 1        | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | DefaultJob | Arm64    | False    | Default                | 1        | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | DefaultJob | Arm64    | False    | Default                | 1        | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | DefaultJob | Arm64    | False    | Default                | 1        | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| NoCaching         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1        | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheMiss         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1        | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1        | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1        | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1        | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1        | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| **NoCaching**         | **DefaultJob** | **Arm64**    | **False**    | **Default**                | **1**        | **Small**     |   **NA** |    **NA** |     **?** |       **?** |    **?** |           **?** |
| CacheMiss         | DefaultJob | Arm64    | False    | Default                | 1        | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | DefaultJob | Arm64    | False    | Default                | 1        | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | DefaultJob | Arm64    | False    | Default                | 1        | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | DefaultJob | Arm64    | False    | Default                | 1        | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | DefaultJob | Arm64    | False    | Default                | 1        | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| NoCaching         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1        | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheMiss         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1        | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1        | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1        | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1        | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1        | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| **NoCaching**         | **DefaultJob** | **Arm64**    | **False**    | **Default**                | **10**       | **Large**     |   **NA** |    **NA** |     **?** |       **?** |    **?** |           **?** |
| CacheMiss         | DefaultJob | Arm64    | False    | Default                | 10       | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | DefaultJob | Arm64    | False    | Default                | 10       | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | DefaultJob | Arm64    | False    | Default                | 10       | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | DefaultJob | Arm64    | False    | Default                | 10       | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | DefaultJob | Arm64    | False    | Default                | 10       | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| NoCaching         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 10       | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheMiss         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 10       | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 10       | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 10       | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 10       | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 10       | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| **NoCaching**         | **DefaultJob** | **Arm64**    | **False**    | **Default**                | **10**       | **Medium**    |   **NA** |    **NA** |     **?** |       **?** |    **?** |           **?** |
| CacheMiss         | DefaultJob | Arm64    | False    | Default                | 10       | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | DefaultJob | Arm64    | False    | Default                | 10       | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | DefaultJob | Arm64    | False    | Default                | 10       | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | DefaultJob | Arm64    | False    | Default                | 10       | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | DefaultJob | Arm64    | False    | Default                | 10       | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| NoCaching         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 10       | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheMiss         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 10       | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 10       | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 10       | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 10       | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 10       | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| **NoCaching**         | **DefaultJob** | **Arm64**    | **False**    | **Default**                | **10**       | **Small**     |   **NA** |    **NA** |     **?** |       **?** |    **?** |           **?** |
| CacheMiss         | DefaultJob | Arm64    | False    | Default                | 10       | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | DefaultJob | Arm64    | False    | Default                | 10       | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | DefaultJob | Arm64    | False    | Default                | 10       | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | DefaultJob | Arm64    | False    | Default                | 10       | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | DefaultJob | Arm64    | False    | Default                | 10       | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| NoCaching         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 10       | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheMiss         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 10       | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 10       | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 10       | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 10       | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 10       | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| **NoCaching**         | **DefaultJob** | **Arm64**    | **False**    | **Default**                | **100**      | **Large**     |   **NA** |    **NA** |     **?** |       **?** |    **?** |           **?** |
| CacheMiss         | DefaultJob | Arm64    | False    | Default                | 100      | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | DefaultJob | Arm64    | False    | Default                | 100      | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | DefaultJob | Arm64    | False    | Default                | 100      | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | DefaultJob | Arm64    | False    | Default                | 100      | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | DefaultJob | Arm64    | False    | Default                | 100      | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| NoCaching         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 100      | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheMiss         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 100      | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 100      | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 100      | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 100      | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 100      | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| **NoCaching**         | **DefaultJob** | **Arm64**    | **False**    | **Default**                | **100**      | **Medium**    |   **NA** |    **NA** |     **?** |       **?** |    **?** |           **?** |
| CacheMiss         | DefaultJob | Arm64    | False    | Default                | 100      | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | DefaultJob | Arm64    | False    | Default                | 100      | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | DefaultJob | Arm64    | False    | Default                | 100      | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | DefaultJob | Arm64    | False    | Default                | 100      | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | DefaultJob | Arm64    | False    | Default                | 100      | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| NoCaching         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 100      | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheMiss         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 100      | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 100      | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 100      | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 100      | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 100      | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| **NoCaching**         | **DefaultJob** | **Arm64**    | **False**    | **Default**                | **100**      | **Small**     |   **NA** |    **NA** |     **?** |       **?** |    **?** |           **?** |
| CacheMiss         | DefaultJob | Arm64    | False    | Default                | 100      | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | DefaultJob | Arm64    | False    | Default                | 100      | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | DefaultJob | Arm64    | False    | Default                | 100      | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | DefaultJob | Arm64    | False    | Default                | 100      | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | DefaultJob | Arm64    | False    | Default                | 100      | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| NoCaching         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 100      | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheMiss         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 100      | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 100      | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 100      | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 100      | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 100      | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| **NoCaching**         | **DefaultJob** | **Arm64**    | **False**    | **Default**                | **1000**     | **Large**     |   **NA** |    **NA** |     **?** |       **?** |    **?** |           **?** |
| CacheMiss         | DefaultJob | Arm64    | False    | Default                | 1000     | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | DefaultJob | Arm64    | False    | Default                | 1000     | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | DefaultJob | Arm64    | False    | Default                | 1000     | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | DefaultJob | Arm64    | False    | Default                | 1000     | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | DefaultJob | Arm64    | False    | Default                | 1000     | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| NoCaching         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1000     | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheMiss         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1000     | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1000     | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1000     | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1000     | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1000     | Large     |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| **NoCaching**         | **DefaultJob** | **Arm64**    | **False**    | **Default**                | **1000**     | **Medium**    |   **NA** |    **NA** |     **?** |       **?** |    **?** |           **?** |
| CacheMiss         | DefaultJob | Arm64    | False    | Default                | 1000     | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | DefaultJob | Arm64    | False    | Default                | 1000     | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | DefaultJob | Arm64    | False    | Default                | 1000     | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | DefaultJob | Arm64    | False    | Default                | 1000     | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | DefaultJob | Arm64    | False    | Default                | 1000     | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| NoCaching         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1000     | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheMiss         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1000     | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1000     | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1000     | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1000     | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1000     | Medium    |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| **NoCaching**         | **DefaultJob** | **Arm64**    | **False**    | **Default**                | **1000**     | **Small**     |   **NA** |    **NA** |     **?** |       **?** |    **?** |           **?** |
| CacheMiss         | DefaultJob | Arm64    | False    | Default                | 1000     | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | DefaultJob | Arm64    | False    | Default                | 1000     | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | DefaultJob | Arm64    | False    | Default                | 1000     | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | DefaultJob | Arm64    | False    | Default                | 1000     | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | DefaultJob | Arm64    | False    | Default                | 1000     | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
|                   |            |          |          |                        |          |           |      |       |       |         |      |             |
| NoCaching         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1000     | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheMiss         | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1000     | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHit          | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1000     | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheHitCold      | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1000     | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| CacheInvalidation | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1000     | Small     |   NA |    NA |     ? |       ? |    ? |           ? |
| MultipleCacheHits | Job-CIIWKT | X64      | True     | InProcessEmitToolchain | 1000     | Small     |   NA |    NA |     ? |       ? |    ? |           ? |

Benchmarks with issues:
  BasicCachingBenchmarks.NoCaching: DefaultJob [DataSize=1, ModelType=Large]
  BasicCachingBenchmarks.CacheMiss: DefaultJob [DataSize=1, ModelType=Large]
  BasicCachingBenchmarks.CacheHit: DefaultJob [DataSize=1, ModelType=Large]
  BasicCachingBenchmarks.CacheHitCold: DefaultJob [DataSize=1, ModelType=Large]
  BasicCachingBenchmarks.CacheInvalidation: DefaultJob [DataSize=1, ModelType=Large]
  BasicCachingBenchmarks.MultipleCacheHits: DefaultJob [DataSize=1, ModelType=Large]
  BasicCachingBenchmarks.NoCaching: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1, ModelType=Large]
  BasicCachingBenchmarks.CacheMiss: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1, ModelType=Large]
  BasicCachingBenchmarks.CacheHit: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1, ModelType=Large]
  BasicCachingBenchmarks.CacheHitCold: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1, ModelType=Large]
  BasicCachingBenchmarks.CacheInvalidation: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1, ModelType=Large]
  BasicCachingBenchmarks.MultipleCacheHits: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1, ModelType=Large]
  BasicCachingBenchmarks.NoCaching: DefaultJob [DataSize=1, ModelType=Medium]
  BasicCachingBenchmarks.CacheMiss: DefaultJob [DataSize=1, ModelType=Medium]
  BasicCachingBenchmarks.CacheHit: DefaultJob [DataSize=1, ModelType=Medium]
  BasicCachingBenchmarks.CacheHitCold: DefaultJob [DataSize=1, ModelType=Medium]
  BasicCachingBenchmarks.CacheInvalidation: DefaultJob [DataSize=1, ModelType=Medium]
  BasicCachingBenchmarks.MultipleCacheHits: DefaultJob [DataSize=1, ModelType=Medium]
  BasicCachingBenchmarks.NoCaching: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1, ModelType=Medium]
  BasicCachingBenchmarks.CacheMiss: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1, ModelType=Medium]
  BasicCachingBenchmarks.CacheHit: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1, ModelType=Medium]
  BasicCachingBenchmarks.CacheHitCold: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1, ModelType=Medium]
  BasicCachingBenchmarks.CacheInvalidation: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1, ModelType=Medium]
  BasicCachingBenchmarks.MultipleCacheHits: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1, ModelType=Medium]
  BasicCachingBenchmarks.NoCaching: DefaultJob [DataSize=1, ModelType=Small]
  BasicCachingBenchmarks.CacheMiss: DefaultJob [DataSize=1, ModelType=Small]
  BasicCachingBenchmarks.CacheHit: DefaultJob [DataSize=1, ModelType=Small]
  BasicCachingBenchmarks.CacheHitCold: DefaultJob [DataSize=1, ModelType=Small]
  BasicCachingBenchmarks.CacheInvalidation: DefaultJob [DataSize=1, ModelType=Small]
  BasicCachingBenchmarks.MultipleCacheHits: DefaultJob [DataSize=1, ModelType=Small]
  BasicCachingBenchmarks.NoCaching: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1, ModelType=Small]
  BasicCachingBenchmarks.CacheMiss: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1, ModelType=Small]
  BasicCachingBenchmarks.CacheHit: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1, ModelType=Small]
  BasicCachingBenchmarks.CacheHitCold: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1, ModelType=Small]
  BasicCachingBenchmarks.CacheInvalidation: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1, ModelType=Small]
  BasicCachingBenchmarks.MultipleCacheHits: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1, ModelType=Small]
  BasicCachingBenchmarks.NoCaching: DefaultJob [DataSize=10, ModelType=Large]
  BasicCachingBenchmarks.CacheMiss: DefaultJob [DataSize=10, ModelType=Large]
  BasicCachingBenchmarks.CacheHit: DefaultJob [DataSize=10, ModelType=Large]
  BasicCachingBenchmarks.CacheHitCold: DefaultJob [DataSize=10, ModelType=Large]
  BasicCachingBenchmarks.CacheInvalidation: DefaultJob [DataSize=10, ModelType=Large]
  BasicCachingBenchmarks.MultipleCacheHits: DefaultJob [DataSize=10, ModelType=Large]
  BasicCachingBenchmarks.NoCaching: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=10, ModelType=Large]
  BasicCachingBenchmarks.CacheMiss: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=10, ModelType=Large]
  BasicCachingBenchmarks.CacheHit: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=10, ModelType=Large]
  BasicCachingBenchmarks.CacheHitCold: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=10, ModelType=Large]
  BasicCachingBenchmarks.CacheInvalidation: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=10, ModelType=Large]
  BasicCachingBenchmarks.MultipleCacheHits: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=10, ModelType=Large]
  BasicCachingBenchmarks.NoCaching: DefaultJob [DataSize=10, ModelType=Medium]
  BasicCachingBenchmarks.CacheMiss: DefaultJob [DataSize=10, ModelType=Medium]
  BasicCachingBenchmarks.CacheHit: DefaultJob [DataSize=10, ModelType=Medium]
  BasicCachingBenchmarks.CacheHitCold: DefaultJob [DataSize=10, ModelType=Medium]
  BasicCachingBenchmarks.CacheInvalidation: DefaultJob [DataSize=10, ModelType=Medium]
  BasicCachingBenchmarks.MultipleCacheHits: DefaultJob [DataSize=10, ModelType=Medium]
  BasicCachingBenchmarks.NoCaching: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=10, ModelType=Medium]
  BasicCachingBenchmarks.CacheMiss: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=10, ModelType=Medium]
  BasicCachingBenchmarks.CacheHit: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=10, ModelType=Medium]
  BasicCachingBenchmarks.CacheHitCold: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=10, ModelType=Medium]
  BasicCachingBenchmarks.CacheInvalidation: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=10, ModelType=Medium]
  BasicCachingBenchmarks.MultipleCacheHits: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=10, ModelType=Medium]
  BasicCachingBenchmarks.NoCaching: DefaultJob [DataSize=10, ModelType=Small]
  BasicCachingBenchmarks.CacheMiss: DefaultJob [DataSize=10, ModelType=Small]
  BasicCachingBenchmarks.CacheHit: DefaultJob [DataSize=10, ModelType=Small]
  BasicCachingBenchmarks.CacheHitCold: DefaultJob [DataSize=10, ModelType=Small]
  BasicCachingBenchmarks.CacheInvalidation: DefaultJob [DataSize=10, ModelType=Small]
  BasicCachingBenchmarks.MultipleCacheHits: DefaultJob [DataSize=10, ModelType=Small]
  BasicCachingBenchmarks.NoCaching: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=10, ModelType=Small]
  BasicCachingBenchmarks.CacheMiss: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=10, ModelType=Small]
  BasicCachingBenchmarks.CacheHit: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=10, ModelType=Small]
  BasicCachingBenchmarks.CacheHitCold: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=10, ModelType=Small]
  BasicCachingBenchmarks.CacheInvalidation: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=10, ModelType=Small]
  BasicCachingBenchmarks.MultipleCacheHits: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=10, ModelType=Small]
  BasicCachingBenchmarks.NoCaching: DefaultJob [DataSize=100, ModelType=Large]
  BasicCachingBenchmarks.CacheMiss: DefaultJob [DataSize=100, ModelType=Large]
  BasicCachingBenchmarks.CacheHit: DefaultJob [DataSize=100, ModelType=Large]
  BasicCachingBenchmarks.CacheHitCold: DefaultJob [DataSize=100, ModelType=Large]
  BasicCachingBenchmarks.CacheInvalidation: DefaultJob [DataSize=100, ModelType=Large]
  BasicCachingBenchmarks.MultipleCacheHits: DefaultJob [DataSize=100, ModelType=Large]
  BasicCachingBenchmarks.NoCaching: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=100, ModelType=Large]
  BasicCachingBenchmarks.CacheMiss: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=100, ModelType=Large]
  BasicCachingBenchmarks.CacheHit: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=100, ModelType=Large]
  BasicCachingBenchmarks.CacheHitCold: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=100, ModelType=Large]
  BasicCachingBenchmarks.CacheInvalidation: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=100, ModelType=Large]
  BasicCachingBenchmarks.MultipleCacheHits: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=100, ModelType=Large]
  BasicCachingBenchmarks.NoCaching: DefaultJob [DataSize=100, ModelType=Medium]
  BasicCachingBenchmarks.CacheMiss: DefaultJob [DataSize=100, ModelType=Medium]
  BasicCachingBenchmarks.CacheHit: DefaultJob [DataSize=100, ModelType=Medium]
  BasicCachingBenchmarks.CacheHitCold: DefaultJob [DataSize=100, ModelType=Medium]
  BasicCachingBenchmarks.CacheInvalidation: DefaultJob [DataSize=100, ModelType=Medium]
  BasicCachingBenchmarks.MultipleCacheHits: DefaultJob [DataSize=100, ModelType=Medium]
  BasicCachingBenchmarks.NoCaching: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=100, ModelType=Medium]
  BasicCachingBenchmarks.CacheMiss: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=100, ModelType=Medium]
  BasicCachingBenchmarks.CacheHit: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=100, ModelType=Medium]
  BasicCachingBenchmarks.CacheHitCold: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=100, ModelType=Medium]
  BasicCachingBenchmarks.CacheInvalidation: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=100, ModelType=Medium]
  BasicCachingBenchmarks.MultipleCacheHits: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=100, ModelType=Medium]
  BasicCachingBenchmarks.NoCaching: DefaultJob [DataSize=100, ModelType=Small]
  BasicCachingBenchmarks.CacheMiss: DefaultJob [DataSize=100, ModelType=Small]
  BasicCachingBenchmarks.CacheHit: DefaultJob [DataSize=100, ModelType=Small]
  BasicCachingBenchmarks.CacheHitCold: DefaultJob [DataSize=100, ModelType=Small]
  BasicCachingBenchmarks.CacheInvalidation: DefaultJob [DataSize=100, ModelType=Small]
  BasicCachingBenchmarks.MultipleCacheHits: DefaultJob [DataSize=100, ModelType=Small]
  BasicCachingBenchmarks.NoCaching: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=100, ModelType=Small]
  BasicCachingBenchmarks.CacheMiss: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=100, ModelType=Small]
  BasicCachingBenchmarks.CacheHit: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=100, ModelType=Small]
  BasicCachingBenchmarks.CacheHitCold: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=100, ModelType=Small]
  BasicCachingBenchmarks.CacheInvalidation: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=100, ModelType=Small]
  BasicCachingBenchmarks.MultipleCacheHits: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=100, ModelType=Small]
  BasicCachingBenchmarks.NoCaching: DefaultJob [DataSize=1000, ModelType=Large]
  BasicCachingBenchmarks.CacheMiss: DefaultJob [DataSize=1000, ModelType=Large]
  BasicCachingBenchmarks.CacheHit: DefaultJob [DataSize=1000, ModelType=Large]
  BasicCachingBenchmarks.CacheHitCold: DefaultJob [DataSize=1000, ModelType=Large]
  BasicCachingBenchmarks.CacheInvalidation: DefaultJob [DataSize=1000, ModelType=Large]
  BasicCachingBenchmarks.MultipleCacheHits: DefaultJob [DataSize=1000, ModelType=Large]
  BasicCachingBenchmarks.NoCaching: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1000, ModelType=Large]
  BasicCachingBenchmarks.CacheMiss: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1000, ModelType=Large]
  BasicCachingBenchmarks.CacheHit: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1000, ModelType=Large]
  BasicCachingBenchmarks.CacheHitCold: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1000, ModelType=Large]
  BasicCachingBenchmarks.CacheInvalidation: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1000, ModelType=Large]
  BasicCachingBenchmarks.MultipleCacheHits: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1000, ModelType=Large]
  BasicCachingBenchmarks.NoCaching: DefaultJob [DataSize=1000, ModelType=Medium]
  BasicCachingBenchmarks.CacheMiss: DefaultJob [DataSize=1000, ModelType=Medium]
  BasicCachingBenchmarks.CacheHit: DefaultJob [DataSize=1000, ModelType=Medium]
  BasicCachingBenchmarks.CacheHitCold: DefaultJob [DataSize=1000, ModelType=Medium]
  BasicCachingBenchmarks.CacheInvalidation: DefaultJob [DataSize=1000, ModelType=Medium]
  BasicCachingBenchmarks.MultipleCacheHits: DefaultJob [DataSize=1000, ModelType=Medium]
  BasicCachingBenchmarks.NoCaching: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1000, ModelType=Medium]
  BasicCachingBenchmarks.CacheMiss: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1000, ModelType=Medium]
  BasicCachingBenchmarks.CacheHit: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1000, ModelType=Medium]
  BasicCachingBenchmarks.CacheHitCold: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1000, ModelType=Medium]
  BasicCachingBenchmarks.CacheInvalidation: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1000, ModelType=Medium]
  BasicCachingBenchmarks.MultipleCacheHits: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1000, ModelType=Medium]
  BasicCachingBenchmarks.NoCaching: DefaultJob [DataSize=1000, ModelType=Small]
  BasicCachingBenchmarks.CacheMiss: DefaultJob [DataSize=1000, ModelType=Small]
  BasicCachingBenchmarks.CacheHit: DefaultJob [DataSize=1000, ModelType=Small]
  BasicCachingBenchmarks.CacheHitCold: DefaultJob [DataSize=1000, ModelType=Small]
  BasicCachingBenchmarks.CacheInvalidation: DefaultJob [DataSize=1000, ModelType=Small]
  BasicCachingBenchmarks.MultipleCacheHits: DefaultJob [DataSize=1000, ModelType=Small]
  BasicCachingBenchmarks.NoCaching: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1000, ModelType=Small]
  BasicCachingBenchmarks.CacheMiss: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1000, ModelType=Small]
  BasicCachingBenchmarks.CacheHit: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1000, ModelType=Small]
  BasicCachingBenchmarks.CacheHitCold: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1000, ModelType=Small]
  BasicCachingBenchmarks.CacheInvalidation: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1000, ModelType=Small]
  BasicCachingBenchmarks.MultipleCacheHits: Job-CIIWKT(Platform=X64, Concurrent=True, RetainVm=True, Server=True, Toolchain=InProcessEmitToolchain) [DataSize=1000, ModelType=Small]
