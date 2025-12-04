# MethodCache.Providers.Memory

[![NuGet](https://img.shields.io/nuget/v/MethodCache.Providers.Memory.svg)](https://www.nuget.org/packages/MethodCache.Providers.Memory)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

Advanced in-memory storage provider for MethodCache with sophisticated eviction policies, memory tracking, and tag-based invalidation support.

## Features

- **High Performance**: Sub-microsecond cache hits with concurrent dictionary
- **Multiple Eviction Policies**: LRU, LFU, TTL, and Random eviction
- **Memory Tracking**: Configurable memory limits with automatic eviction
- **Tag Support**: Efficient tag-based cache invalidation
- **Detailed Metrics**: Hit/miss ratios, memory usage, and operation counters
- **Background Cleanup**: Automatic expired entry removal with adaptive frequency
- **Memory Pressure Handling**: Automatic cleanup frequency adjustment under pressure

## Quick Start

### Installation

```bash
dotnet add package MethodCache.Providers.Memory
```

### Basic Configuration

```csharp
using MethodCache.Providers.Memory.Extensions;

// Simple in-memory caching
services.AddAdvancedMemoryStorage();

// With custom configuration
services.AddAdvancedMemoryStorage(options =>
{
    options.MaxMemoryUsage = 100 * 1024 * 1024; // 100 MB
    options.EvictionPolicy = EvictionPolicy.LRU;
    options.EnableAutomaticCleanup = true;
});
```

### Usage with MethodCache

```csharp
// Register with MethodCache core
services.AddMethodCache();
services.AddAdvancedMemoryStorage(options =>
{
    options.MaxMemoryUsage = 50 * 1024 * 1024; // 50 MB
    options.EvictionPolicy = EvictionPolicy.LRU;
    options.CleanupInterval = TimeSpan.FromMinutes(5);
});

// Use in services
public class ProductService
{
    [Cache(Duration = "1h", Tags = new[] { "products" })]
    public async Task<Product> GetProductAsync(int id)
    {
        return await _repository.GetProductAsync(id);
    }
}
```

## Configuration Options

```csharp
public class AdvancedMemoryOptions
{
    /// <summary>
    /// Maximum number of entries to store in memory.
    /// </summary>
    public long MaxEntries { get; set; } = 100000;

    /// <summary>
    /// Maximum memory usage in bytes (approximate).
    /// Default: 256MB
    /// </summary>
    public long MaxMemoryUsage { get; set; } = 256 * 1024 * 1024;

    /// <summary>
    /// Eviction policy to use when limits are reached.
    /// </summary>
    public EvictionPolicy EvictionPolicy { get; set; } = EvictionPolicy.LRU;

    /// <summary>
    /// How often to run cleanup for expired entries.
    /// Under high memory pressure, cleanup can occur more frequently.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Minimum cleanup interval when under memory pressure.
    /// </summary>
    public TimeSpan MinCleanupInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Memory pressure threshold (0.0 to 1.0) at which to increase cleanup frequency.
    /// Default is 0.8 (80%).
    /// </summary>
    public double MemoryPressureThreshold { get; set; } = 0.8;

    /// <summary>
    /// Memory usage calculation mode: Estimated (faster) or Accurate (higher overhead).
    /// </summary>
    public MemoryUsageCalculationMode MemoryCalculationMode { get; set; } = MemoryUsageCalculationMode.Accurate;

    /// <summary>
    /// Maximum number of tag mappings to prevent unbounded growth.
    /// </summary>
    public int MaxTagMappings { get; set; } = 100000;

    /// <summary>
    /// Whether to enable detailed statistics tracking.
    /// </summary>
    public bool EnableDetailedStats { get; set; } = true;

    /// <summary>
    /// Whether to enable automatic cleanup of expired entries.
    /// </summary>
    public bool EnableAutomaticCleanup { get; set; } = true;
}
```

## Eviction Policies

### LRU (Least Recently Used) - Default
Evicts entries that haven't been accessed for the longest time. Best for most scenarios.

```csharp
options.EvictionPolicy = EvictionPolicy.LRU;
```

### LFU (Least Frequently Used)
Evicts entries with the lowest access count. Good for hot-spot scenarios.

```csharp
options.EvictionPolicy = EvictionPolicy.LFU;
```

### TTL (Time To Live)
Evicts entries that are closest to expiration. Good for time-sensitive data.

```csharp
options.EvictionPolicy = EvictionPolicy.TTL;
```

### Random
Evicts random entries. Simple and fast, no tracking overhead.

```csharp
options.EvictionPolicy = EvictionPolicy.Random;
```

## Advanced Features

### Memory Pressure Handling

The provider automatically handles memory pressure by increasing cleanup frequency:

```csharp
services.AddAdvancedMemoryStorage(options =>
{
    options.MaxMemoryUsage = 100 * 1024 * 1024; // 100 MB limit
    options.MemoryPressureThreshold = 0.8; // Start aggressive cleanup at 80%
    options.MinCleanupInterval = TimeSpan.FromSeconds(30); // Minimum cleanup interval
    options.MemoryCalculationMode = MemoryUsageCalculationMode.Accurate;
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

## Comparison with Other Providers

| Feature | Memory | Redis | SQL Server |
|---------|--------|-------|------------|
| **Latency** | < 1μs | 1-5ms | 5-20ms |
| **Throughput** | Very High | High | Medium |
| **Persistence** | No | Yes | Yes |
| **Distributed** | No | Yes | Yes |
| **Memory Efficient** | Yes | No | No |
| **Tag Support** | Yes | Yes | Yes |
| **No Dependencies** | Yes | No | No |

## Best Practices

### 1. Choose Appropriate Limits

```csharp
// For small applications
options.MaxMemoryUsage = 50 * 1024 * 1024; // 50 MB
options.MaxEntries = 5000;

// For large applications
options.MaxMemoryUsage = 500 * 1024 * 1024; // 500 MB
options.MaxEntries = 50000;
```

### 2. Select Right Eviction Policy

- **Web APIs**: LRU (handles varying access patterns well)
- **Read-heavy with hot data**: LFU (keeps frequently accessed data)
- **Time-sensitive data**: TTL (evicts near-expiration entries first)
- **Unpredictable patterns**: Random (lowest overhead)

### 3. Use Tags Wisely

```csharp
// Good: Hierarchical, meaningful tags
Tags = new[] { "products", "category:electronics", "store:west" }

// Bad: Too many unique tags
Tags = new[] { $"timestamp:{DateTime.Now.Ticks}" } // Creates unique tag per entry!
```

## Limitations

- **No Persistence**: Data is lost on application restart
- **Single Process**: Not suitable for distributed scenarios
- **Memory Bound**: Limited by available process memory
- **No Replication**: No built-in redundancy

For distributed scenarios, consider:
- `MethodCache.Providers.Redis` for distributed caching
- `MethodCache.Providers.SqlServer` for persistent caching

## License

MIT License - see [LICENSE](../LICENSE) file for details.

## Related Packages

- [MethodCache](https://www.nuget.org/packages/MethodCache) - Meta-package with everything included
- [MethodCache.Core](https://www.nuget.org/packages/MethodCache.Core) - Core caching functionality
- [MethodCache.Providers.Redis](https://www.nuget.org/packages/MethodCache.Providers.Redis) - Redis distributed caching
