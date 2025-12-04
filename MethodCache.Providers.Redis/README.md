# MethodCache.Providers.Redis

[![NuGet](https://img.shields.io/nuget/v/MethodCache.Providers.Redis.svg)](https://www.nuget.org/packages/MethodCache.Providers.Redis)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

Distributed Redis provider for MethodCache with hybrid L1/L2 orchestration, distributed locking, compression, and tag-based invalidation.

## Features

- **Drop-in Replacement**: Single registration swaps providers
- **Hybrid-Ready**: L1/L2 strategies with automatic coordination
- **Resilience Built-in**: Retry policies and circuit breaker
- **Tag Invalidation**: Redis sets with efficient reverse indexing
- **Serialization**: MessagePack (default), JSON, or custom
- **Compression**: None, Gzip, or Brotli with configurable threshold
- **Distributed Locking**: Built-in support for stampede protection
- **PubSub Invalidation**: Cross-instance cache coordination

## Quick Start

### Installation

```bash
dotnet add package MethodCache.Providers.Redis
```

### Basic Configuration

```csharp
using MethodCache.Providers.Redis.Extensions;

services.AddRedisCache(options =>
{
    options.ConnectionString = "localhost:6379";
    options.KeyPrefix = "myapp:";
});
```

### With MethodCache Fluent API

```csharp
services.AddRedisCache(options =>
{
    options.ConnectionString = "localhost:6379";
});

services.AddMethodCacheFluent(fluent =>
{
    fluent.ForService<IUserService>()
          .Method(s => s.GetUserAsync(default))
          .Configure(o => o
              .WithDuration(TimeSpan.FromMinutes(10))
              .WithTags("users"))
          .RequireIdempotent();
});
```

### Hybrid L1+L2 Cache

```csharp
services.AddRedisHybridInfrastructure(options =>
{
    options.ConnectionString = "localhost:6379";
    options.EnablePubSubInvalidation = true;
});
```

## Configuration Options

```csharp
services.AddRedisCache(options =>
{
    // Connection
    options.ConnectionString = "redis:6379";
    options.DatabaseNumber = 0;
    options.KeyPrefix = "myapp:";
    options.DefaultExpiration = TimeSpan.FromMinutes(30);

    // Connection Management
    options.MaxConnections = 100;
    options.ConnectTimeout = TimeSpan.FromSeconds(5);
    options.SyncTimeout = TimeSpan.FromSeconds(5);

    // Serialization
    options.DefaultSerializer = RedisSerializerType.MessagePack; // MessagePack, Json, Binary
    options.Compression = RedisCompressionType.None; // None, Gzip, Brotli
    options.CompressionThreshold = 1024; // Compress values larger than 1KB

    // Resilience
    options.Retry = new RetryOptions
    {
        MaxRetries = 3,
        BaseDelay = TimeSpan.FromMilliseconds(100),
        BackoffType = RetryBackoffType.ExponentialWithJitter
    };

    options.CircuitBreaker = new CircuitBreakerOptions
    {
        FailureRatio = 0.5,
        MinimumThroughput = 10,
        BreakDuration = TimeSpan.FromSeconds(30)
    };

    // Advanced Features
    options.EnableDistributedLocking = true;
    options.EnablePubSubInvalidation = false;
    options.EnableCacheWarming = false;
    options.BackplaneChannel = "methodcache-shared";

    // Monitoring
    options.EnableDetailedMetrics = true;
    options.EnableSlowLogMonitoring = false;
});
```

## Serialization Options

### MessagePack (Default, Recommended)
- Best performance and smallest payload
- Binary format, cross-platform
- Requires `[MessagePackObject]` attribute on custom types

### JSON
- Human-readable format
- Good for debugging
- Slightly larger payload

### Binary
- Uses System.Text.Json internally
- Compatible with most .NET types

## Compression

Enable compression for large values:

```csharp
options.Compression = RedisCompressionType.Gzip;
options.CompressionThreshold = 1024; // Only compress values > 1KB
```

Available types: `None`, `Gzip`, `Brotli`

## Health Checks

```csharp
services.AddRedisInfrastructureWithHealthChecks(
    options => { options.ConnectionString = "localhost:6379"; },
    healthCheckName: "redis_cache");

app.MapHealthChecks("/health");
```

## Migration from In-Memory

```csharp
// Before
services.AddMethodCache();

// After
services.AddRedisCache(options =>
{
    options.ConnectionString = "localhost:6379";
});
```

All attributes, fluent rules, and configuration files continue to work without modification.

## Best Practices

### Redis Setup
- Use clustering or Sentinel for HA
- Enable persistence (RDB/AOF) for durability
- Monitor memory and evictions

### Security
- Enable AUTH and TLS
- Restrict network access
- Keep Redis patched

### Performance
- Use MessagePack serialization
- Enable compression for large values
- Configure appropriate connection pool size

## Troubleshooting

### Connection Failures
- Verify connection string format
- Check Docker port mapping
- Verify firewall rules
- Check TLS settings if enabled

### High Latency
- Check network distance to Redis
- Tune retry policy
- Monitor Redis CPU usage
- Enable command pipelining

### Cache Misses
- Verify key generation (check versioning/prefix)
- Check expiration settings
- Review tag invalidation logic

## Comparison with Other Providers

| Feature | Redis | Memory | SQL Server |
|---------|-------|--------|------------|
| **Latency** | 1-5ms | < 1μs | 5-20ms |
| **Distributed** | Yes | No | Yes |
| **Persistence** | Optional | No | Yes |
| **PubSub** | Yes | No | Polling |
| **Compression** | Yes | No | No |

## License

MIT License - see [LICENSE](../LICENSE) file for details.

## Related Packages

- [MethodCache](https://www.nuget.org/packages/MethodCache) - Meta-package with everything included
- [MethodCache.Core](https://www.nuget.org/packages/MethodCache.Core) - Core caching functionality
- [MethodCache.Providers.Memory](https://www.nuget.org/packages/MethodCache.Providers.Memory) - Advanced in-memory caching
- [MethodCache.Providers.SqlServer](https://www.nuget.org/packages/MethodCache.Providers.SqlServer) - SQL Server persistent caching
