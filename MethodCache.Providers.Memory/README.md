# MethodCache.Providers.Memory

[![NuGet](https://img.shields.io/nuget/v/MethodCache.Providers.Memory.svg)](https://www.nuget.org/packages/MethodCache.Providers.Memory)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

Advanced in-memory storage provider for MethodCache with sophisticated eviction policies, memory tracking, and tag-based invalidation support.

## Features

- 🚀 **High Performance**: Sub-microsecond response times with concurrent dictionary
- 🎯 **Multiple Eviction Policies**: LRU, LFU, MRU, Random, and Time-based eviction
- 📊 **Memory Tracking**: Configurable memory limits with automatic eviction
- 🏷️ **Tag Support**: Efficient tag-based cache invalidation
- 📈 **Detailed Metrics**: Hit/miss ratios, memory usage, and operation counters
- 🔄 **Background Cleanup**: Automatic expired entry removal
- 🧩 **Infrastructure Pattern**: Clean integration with MethodCache.Infrastructure

## Quick Start

### Installation

```bash
dotnet add package MethodCache.Providers.Memory
```

### Basic Configuration

```csharp
using MethodCache.Providers.Memory.Extensions;

// Simple in-memory caching
services.AddAdvancedMemoryInfrastructure();

// With custom configuration
services.AddAdvancedMemoryInfrastructure(options =>
{
    options.MaxMemorySize = 100 * 1024 * 1024; // 100 MB
    options.EvictionPolicy = MemoryEvictionPolicy.LRU;
    options.EnableBackgroundCleanup = true;
});
```

### Usage with MethodCache

```csharp
// Register with MethodCache core
services.AddMethodCache()
    .AddAdvancedMemoryInfrastructure(options =>
    {
        options.MaxMemorySize = 50 * 1024 * 1024; // 50 MB
        options.EvictionPolicy = MemoryEvictionPolicy.LRU;
        options.CleanupInterval = TimeSpan.FromMinutes(5);
    });

// Use in services
public class ProductService
{
    [Cache(Duration = "1h", Tags = ["products"])]
    public async Task<Product> GetProductAsync(int id)
    {
        return await _repository.GetProductAsync(id);
    }
}
```

## Configuration Options

```csharp
public class MemoryStorageOptions
{
    /// <summary>
    /// Maximum memory size in bytes (0 = unlimited)
    /// </summary>
    public long MaxMemorySize { get; set; } = 100 * 1024 * 1024; // 100 MB

    /// <summary>
    /// Maximum number of entries (0 = unlimited)
    /// </summary>
    public int MaxEntries { get; set; } = 10000;

    /// <summary>
    /// Eviction policy when limits are reached
    /// </summary>
    public MemoryEvictionPolicy EvictionPolicy { get; set; } = MemoryEvictionPolicy.LRU;

    /// <summary>
    /// Enable automatic background cleanup
    /// </summary>
    public bool EnableBackgroundCleanup { get; set; } = true;

    /// <summary>
    /// Interval between cleanup runs
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Enable detailed memory tracking (has performance impact)
    /// </summary>
    public bool EnableMemoryTracking { get; set; } = true;

    /// <summary>
    /// Percentage of memory to free when limit is reached
    /// </summary>
    public double EvictionPercentage { get; set; } = 0.1; // 10%
}
```

## Eviction Policies

### LRU (Least Recently Used) - Default
Evicts entries that haven't been accessed for the longest time. Best for most scenarios.

```csharp
options.EvictionPolicy = MemoryEvictionPolicy.LRU;
```

### LFU (Least Frequently Used)
Evicts entries with the lowest access count. Good for hot-spot scenarios.

```csharp
options.EvictionPolicy = MemoryEvictionPolicy.LFU;
```

### MRU (Most Recently Used)
Evicts the most recently accessed entries. Useful for cyclic access patterns.

```csharp
options.EvictionPolicy = MemoryEvictionPolicy.MRU;
```

### Random
Evicts random entries. Simple and fast, no tracking overhead.

```csharp
options.EvictionPolicy = MemoryEvictionPolicy.Random;
```

### Time-Based
Evicts oldest entries first (FIFO). Good for time-sensitive data.

```csharp
options.EvictionPolicy = MemoryEvictionPolicy.TimeBased;
```

## Advanced Features

### Memory Pressure Handling

The provider automatically handles memory pressure:

```csharp
services.AddAdvancedMemoryInfrastructure(options =>
{
    options.MaxMemorySize = 100 * 1024 * 1024; // 100 MB limit
    options.EvictionPercentage = 0.2; // Free 20% when limit reached
    options.EnableMemoryTracking = true; // Track actual memory usage
});
```

### Tag-Based Invalidation

Efficiently invalidate groups of related cache entries:

```csharp
// Cache with tags
await storageProvider.SetAsync("user:123", userData, expiration, new[] { "users", "tenant:456" });
await storageProvider.SetAsync("user:124", userData2, expiration, new[] { "users", "tenant:456" });

// Invalidate all users in tenant
await storageProvider.RemoveByTagAsync("tenant:456");
```

### Performance Metrics

Access detailed performance metrics:

```csharp
var stats = await storageProvider.GetStatsAsync();

Console.WriteLine($"Hit Rate: {stats.HitRate:P2}");
Console.WriteLine($"Memory Usage: {stats.MemoryUsageBytes:N0} bytes");
Console.WriteLine($"Entry Count: {stats.EntryCount:N0}");
Console.WriteLine($"Eviction Count: {stats.EvictionCount:N0}");
```

## Performance Characteristics

### Benchmarks (Intel i7, 16GB RAM)

| Operation | Throughput | Latency (p50) | Latency (p99) |
|-----------|------------|---------------|---------------|
| Get (Hit) | 15M ops/sec | 65 ns | 150 ns |
| Get (Miss) | 20M ops/sec | 50 ns | 100 ns |
| Set | 8M ops/sec | 125 ns | 300 ns |
| Remove | 12M ops/sec | 85 ns | 200 ns |
| Tag Invalidation | 500K ops/sec | 2 μs | 10 μs |

### Memory Overhead

- **Base overhead**: ~64 bytes per entry
- **With LRU tracking**: +16 bytes per entry
- **With tags**: +40 bytes per tag association
- **With memory tracking**: +8 bytes per entry

## Comparison with Other Providers

| Feature | Memory | Redis | SQL Server |
|---------|--------|-------|------------|
| **Latency** | < 1μs | 1-5ms | 5-20ms |
| **Throughput** | Very High | High | Medium |
| **Persistence** | ❌ | ✅ | ✅ |
| **Distributed** | ❌ | ✅ | ✅ |
| **Memory Efficient** | ✅ | ❌ | ❌ |
| **Tag Support** | ✅ | ✅ | ✅ |
| **No Dependencies** | ✅ | ❌ | ❌ |

## Best Practices

### 1. Choose Appropriate Limits

```csharp
// For small applications
options.MaxMemorySize = 50 * 1024 * 1024; // 50 MB
options.MaxEntries = 5000;

// For large applications
options.MaxMemorySize = 500 * 1024 * 1024; // 500 MB
options.MaxEntries = 50000;
```

### 2. Select Right Eviction Policy

- **Web APIs**: LRU (handles varying access patterns well)
- **Read-heavy with hot data**: LFU (keeps frequently accessed data)
- **Streaming/Sequential**: MRU or Time-Based
- **Unpredictable**: Random (lowest overhead)

### 3. Monitor Memory Usage

```csharp
// Enable monitoring
services.Configure<MemoryStorageOptions>(options =>
{
    options.EnableMemoryTracking = true;
});

// Add health checks
services.AddHealthChecks()
    .AddCheck<MemoryStorageHealthCheck>("memory_cache");
```

### 4. Use Tags Wisely

```csharp
// Good: Hierarchical, meaningful tags
Tags = ["products", "category:electronics", "store:west"]

// Bad: Too many unique tags
Tags = [$"timestamp:{DateTime.Now.Ticks}"] // Creates unique tag per entry!
```

## Limitations

- **No Persistence**: Data is lost on application restart
- **Single Process**: Not suitable for distributed scenarios
- **Memory Bound**: Limited by available process memory
- **No Replication**: No built-in redundancy

For distributed scenarios, consider:
- `MethodCache.Providers.Redis` for distributed caching
- `MethodCache.Providers.SqlServer` for persistent caching

## Migration from MemoryCache

If migrating from standard `IMemoryCache`:

```csharp
// Before (IMemoryCache)
services.AddMemoryCache();
_memoryCache.Set("key", value, TimeSpan.FromHours(1));

// After (Advanced Memory Provider)
services.AddAdvancedMemoryInfrastructure();
await _storageProvider.SetAsync("key", value, TimeSpan.FromHours(1));
```

Key differences:
- Async API throughout
- Built-in tag support
- Better memory management
- Detailed metrics
- Multiple eviction policies

## License

MIT License - see [LICENSE](LICENSE) file for details.

## Support

- 📚 [Documentation](https://github.com/methodcache/methodcache/wiki)
- 🐛 [Issues](https://github.com/methodcache/methodcache/issues)
- 💬 [Discussions](https://github.com/methodcache/methodcache/discussions)