# MethodCache.Providers.SqlServer

[![NuGet](https://img.shields.io/nuget/v/MethodCache.Providers.SqlServer.svg)](https://www.nuget.org/packages/MethodCache.Providers.SqlServer)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

SQL Server storage provider for MethodCache with automatic table management, resilience policies, tag-based invalidation, and backplane coordination.

## Features

- **SQL Server Storage**: Enterprise-grade distributed caching
- **Auto Table Management**: Automatic schema and table creation
- **Resilience Policies**: Retry logic and circuit breaker patterns
- **Tag Support**: Efficient tag-based cache invalidation with indexes
- **Health Monitoring**: Built-in health checks
- **Multiple Serializers**: JSON, MessagePack, and Binary
- **Hybrid Cache Ready**: L1+L2/L3 caching integration
- **Background Cleanup**: Automatic expired entry removal
- **Backplane Support**: SQL-based cross-instance coordination

## Quick Start

### Installation

```bash
dotnet add package MethodCache.Providers.SqlServer
```

### Basic Configuration

```csharp
using MethodCache.Providers.SqlServer.Extensions;

services.AddSqlServerCache(options =>
{
    options.ConnectionString = "Server=localhost;Database=MyApp;Trusted_Connection=true;";
    options.Schema = "cache";
    options.DefaultSerializer = SqlServerSerializerType.MessagePack;
});
```

### Hybrid L1+L3 Cache

```csharp
services.AddHybridSqlServerCache(
    connectionString: "Server=localhost;Database=MyApp;Trusted_Connection=true;",
    configureHybridOptions: options =>
    {
        options.L1DefaultExpiration = TimeSpan.FromMinutes(5);
        options.L2DefaultExpiration = TimeSpan.FromHours(4);
    });
```

### Complete Setup (Recommended)

```csharp
services.AddSqlServerHybridCacheComplete(
    connectionString: "Server=localhost;Database=MyApp;Trusted_Connection=true;",
    configure: options =>
    {
        options.Schema = "cache";
        options.L1DefaultExpiration = TimeSpan.FromMinutes(5);
        options.L2DefaultExpiration = TimeSpan.FromHours(4);
        options.EnableAutoTableCreation = true;
        options.SqlServerSerializer = SqlServerSerializerType.MessagePack;
        options.EnableBackplane = true;
    });
```

## Configuration Options

```csharp
services.AddSqlServerInfrastructure(options =>
{
    // Connection
    options.ConnectionString = "your-connection-string";
    options.Schema = "cache";
    options.EntriesTableName = "Entries";
    options.TagsTableName = "Tags";
    options.InvalidationsTableName = "Invalidations";
    options.KeyPrefix = "methodcache:";

    // Serialization
    options.DefaultSerializer = SqlServerSerializerType.MessagePack; // Json, MessagePack, Binary

    // Timeouts
    options.CommandTimeoutSeconds = 30;
    options.ConnectionTimeoutSeconds = 15;
    options.HealthCheckTimeoutSeconds = 5;
    options.IsolationLevel = IsolationLevel.ReadCommitted;

    // Resilience
    options.MaxRetryAttempts = 3;
    options.RetryBaseDelay = TimeSpan.FromMilliseconds(500);
    options.RetryBackoffType = SqlServerRetryBackoffType.Exponential; // Linear, Exponential
    options.CircuitBreakerFailureRatio = 0.5;
    options.CircuitBreakerMinimumThroughput = 10;
    options.CircuitBreakerBreakDuration = TimeSpan.FromSeconds(30);

    // Maintenance
    options.EnableAutoTableCreation = true;
    options.EnableBackgroundCleanup = true;
    options.CleanupInterval = TimeSpan.FromMinutes(5);
    options.CleanupBatchSize = 1000;
    options.EnableDetailedLogging = false;

    // Backplane
    options.EnableBackplane = false;
    options.BackplanePollingInterval = TimeSpan.FromSeconds(2);
    options.BackplaneMessageRetention = TimeSpan.FromHours(1);
});
```

## Database Schema

The provider automatically creates these tables when `EnableAutoTableCreation = true`:

### Cache Entries Table
```sql
CREATE TABLE [cache].[Entries] (
    [Key] NVARCHAR(450) NOT NULL PRIMARY KEY,
    [Value] VARBINARY(MAX) NOT NULL,
    [ExpiresAt] DATETIME2 NULL,
    [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [UpdatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE()
)
```

### Cache Tags Table
```sql
CREATE TABLE [cache].[Tags] (
    [Key] NVARCHAR(450) NOT NULL,
    [Tag] NVARCHAR(200) NOT NULL,
    PRIMARY KEY ([Key], [Tag]),
    FOREIGN KEY ([Key]) REFERENCES [cache].[Entries] ([Key]) ON DELETE CASCADE
)
```

### Performance Indexes
- `IX_cache_Entries_ExpiresAt` - Efficient expired entry cleanup
- `IX_cache_Tags_Tag` - Fast tag-based lookups
- `IX_cache_Entries_CreatedAt` - Time-based queries

## Usage Examples

### Method Caching

```csharp
public class UserService
{
    [Cache(Duration = "1h", Tags = new[] { "users" })]
    public async Task<User> GetUserAsync(int userId)
    {
        return await _repository.GetUserAsync(userId);
    }

    [Cache(Duration = "30m", Tags = new[] { "users", "profiles" })]
    public async Task<UserProfile> GetUserProfileAsync(int userId)
    {
        return await _repository.GetUserProfileAsync(userId);
    }
}
```

### Direct Storage Access

```csharp
public class CacheService
{
    private readonly IStorageProvider _storageProvider;

    public async Task<T?> GetAsync<T>(string key)
    {
        return await _storageProvider.GetAsync<T>(key);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan expiration, params string[] tags)
    {
        await _storageProvider.SetAsync(key, value, expiration, tags);
    }

    public async Task InvalidateByTagAsync(string tag)
    {
        await _storageProvider.RemoveByTagAsync(tag);
    }
}
```

### Health Checks

```csharp
services.AddSqlServerInfrastructureWithHealthChecks(
    options => { options.ConnectionString = connectionString; },
    healthCheckName: "sql_server_cache");

app.MapHealthChecks("/health");
```

## Serialization Support

### MessagePack (Recommended)
- Best performance and smallest size
- Binary format, cross-platform
- Requires types to be marked with `[MessagePackObject]`

### JSON
- Human-readable format
- Good for debugging and interoperability
- Slightly larger payload

### Binary
- Uses System.Text.Json as fallback
- Compatible with most .NET types

## Multi-Layer Caching

### L1 + L3 (Memory + SQL Server)

```csharp
services.AddL1L3CacheWithSqlServer(
    configureStorage: options => { /* storage options */ },
    configureSqlServer: options =>
    {
        options.ConnectionString = connectionString;
    });
```

### Triple Layer (L1 + L2 + L3)

```csharp
services.AddTripleLayerCacheWithSqlServer(
    configureStorage: options => { /* storage options */ },
    configureSqlServer: options =>
    {
        options.ConnectionString = connectionString;
    });
```

## Best Practices

### Connection String

```csharp
// Recommended settings
"Server=your-server;Database=your-db;Trusted_Connection=true;MultipleActiveResultSets=true;Connection Timeout=15;Command Timeout=30;"
```

### Schema Design
- Use a dedicated schema (e.g., `cache`) for isolation
- Grant appropriate permissions to the application user
- Consider partitioning for very large datasets

### Tag Strategy

```csharp
// Good: Hierarchical, meaningful tags
Tags = new[] { "user:123", "department:sales", "role:admin" }

// Avoid: Overly broad tags
Tags = new[] { "data" } // Too broad - will invalidate everything
```

## Troubleshooting

### Connection Failures
- Verify connection string and SQL Server accessibility
- Check firewall and network connectivity
- Ensure SQL Server allows remote connections

### Table Creation Errors
- Verify database permissions (DDL rights required)
- Check if schema exists and is accessible
- Review SQL Server error logs

### Performance Issues
- Monitor SQL Server performance counters
- Check for missing indexes
- Review cleanup job frequency and batch sizes

### Tag Invalidation Not Working
- Verify foreign key constraints are set up
- Check for transaction isolation issues
- Monitor for deadlocks in high-concurrency scenarios

## Migration from Other Providers

### From Redis

```csharp
// Before (Redis)
services.AddRedisCache(options =>
{
    options.ConnectionString = "localhost:6379";
});

// After (SQL Server)
services.AddSqlServerCache(options =>
{
    options.ConnectionString = "Server=localhost;Database=MyApp;Trusted_Connection=true;";
});
```

### From In-Memory

```csharp
// Before
services.AddMethodCache();

// After (SQL Server with L1+L3)
services.AddHybridSqlServerCache(connectionString, options =>
{
    options.L1DefaultExpiration = TimeSpan.FromMinutes(5);
    options.L2DefaultExpiration = TimeSpan.FromHours(4);
});
```

## Comparison with Other Providers

| Feature | SQL Server | Redis | Memory |
|---------|------------|-------|--------|
| **Latency** | 5-20ms | 1-5ms | < 1μs |
| **Persistence** | Yes | Optional | No |
| **Distributed** | Yes | Yes | No |
| **Backplane** | Polling | PubSub | N/A |
| **Large Values** | Yes | Limited | Yes |
| **Transactions** | Yes | Limited | No |

## License

MIT License - see [LICENSE](../LICENSE) file for details.

## Related Packages

- [MethodCache](https://www.nuget.org/packages/MethodCache) - Meta-package with everything included
- [MethodCache.Core](https://www.nuget.org/packages/MethodCache.Core) - Core caching functionality
- [MethodCache.Providers.Memory](https://www.nuget.org/packages/MethodCache.Providers.Memory) - Advanced in-memory caching
- [MethodCache.Providers.Redis](https://www.nuget.org/packages/MethodCache.Providers.Redis) - Redis distributed caching
