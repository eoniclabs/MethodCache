# 📊 MethodCache Performance Dashboard

> **Note**: This page describes the scenarios of the performance dashboard benchmarks. It holds no results; for
> measured numbers, with the hardware and versions they were taken on, see [Benchmarks](../benchmarks.md).

## 📝 Methodology

Benchmarks test the following scenarios:
- **No Caching**: Direct method execution without caching
- **Cache Miss**: First-time cache access (cache + method execution)
- **Cache Hit**: Subsequent cache access (cache retrieval only)
- **Cache Hit Cold**: Cache access without warmup
- **Cache Invalidation**: Cache clearing performance

Each benchmark runs with:
- 3 warmup iterations
- 5 measurement iterations
- Memory diagnostics enabled
- Multiple data sizes (1, 10, 100, 1000 items)
- Different model complexities (Small, Medium, Large)

---
*Raw benchmark results, when collected, go to [.performance-data/](../../.performance-data/).*
