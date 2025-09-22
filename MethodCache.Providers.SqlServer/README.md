# MethodCache.Providers.SqlServer

[![NuGet](https://img.shields.io/nuget/v/MethodCache.Providers.SqlServer.svg)](https://www.nuget.org/packages/MethodCache.Providers.SqlServer)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

SQL Server storage provider for MethodCache using the Infrastructure layer pattern. Provides distributed caching capabilities with automatic table management, resilience policies, and comprehensive health monitoring.

## Features

- 🗄️ **SQL Server Storage**: Enterprise-grade distributed caching using SQL Server
- 🏗️ **Infrastructure Pattern**: Clean abstraction layer following MethodCache architecture
- 🔧 **Auto Table Management**: Automatic database schema and table creation
- 🔄 **Resilience Policies**: Built-in retry logic and circuit breaker patterns
- 🏷️ **Tag Support**: Efficient tag-based cache invalidation with optimized indexes
- 🩺 **Health Monitoring**: Comprehensive health checks and performance metrics
- 📦 **Multiple Serializers**: Support for JSON, MessagePack, and Binary serialization
- 🔄 **Hybrid Cache Ready**: Seamless L1+L2 caching integration
- 🧹 **Background Cleanup**: Automatic expired entry cleanup

## Quick Start

### 1. Installation

```bash
dotnet add package MethodCache.Providers.SqlServer
```

### 2. Basic Configuration

```csharp
using MethodCache.Providers.SqlServer.Extensions;

// Basic SQL Server caching
services.AddSqlServerCache(options =>
{
    options.ConnectionString = "Server=localhost;Database=MyApp;Trusted_Connection=true;";
    options.Schema = "cache";
    options.DefaultSerializer = SqlServerSerializerType.MessagePack;
});
```

### 3. Hybrid L1+L2 Configuration

```csharp
// Hybrid cache with SQL Server as L2
services.AddHybridSqlServerCache(
    connectionString: "Server=localhost;Database=MyApp;Trusted_Connection=true;",
    configureHybrid: options =>
    {
        options.L1DefaultExpiration = TimeSpan.FromMinutes(5);
        options.L2DefaultExpiration = TimeSpan.FromHours(4);
        options.Strategy = HybridStrategy.WriteThrough;
    },
    configureSqlServer: options =>
    {
        options.Schema = "cache";
        options.DefaultSerializer = SqlServerSerializerType.MessagePack;
        options.EnableAutoTableCreation = true;
    });
```

### 4. Complete Setup (Recommended)

```csharp
// One-line setup with sensible defaults
services.AddSqlServerHybridCacheComplete(
    connectionString: "Server=localhost;Database=MyApp;Trusted_Connection=true;",
    configure: options =>
    {
        options.Schema = "cache";
        options.L1DefaultExpiration = TimeSpan.FromMinutes(5);
        options.L2DefaultExpiration = TimeSpan.FromHours(4);
        options.EnableAutoTableCreation = true;
        options.SqlServerSerializer = SqlServerSerializerType.MessagePack;
    });
```

## Database Schema

The provider automatically creates the following tables when `EnableAutoTableCreation = true`:

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

## Configuration Options

```csharp
services.AddSqlServerInfrastructure(options =>
{
    // Connection settings
    options.ConnectionString = "your-connection-string";
    options.Schema = "cache";
    options.EntriesTableName = "Entries";
    options.TagsTableName = "Tags";

    // Serialization
    options.DefaultSerializer = SqlServerSerializerType.MessagePack;
    options.KeyPrefix = "methodcache:";

    // Performance
    options.CommandTimeoutSeconds = 30;
    options.ConnectionTimeoutSeconds = 15;
    options.IsolationLevel = IsolationLevel.ReadCommitted;

    // Resilience
    options.MaxRetryAttempts = 3;
    options.RetryBaseDelay = TimeSpan.FromMilliseconds(500);
    options.RetryBackoffType = SqlServerRetryBackoffType.Exponential;
    options.CircuitBreakerFailureRatio = 0.5;
    options.CircuitBreakerMinimumThroughput = 10;
    options.CircuitBreakerBreakDuration = TimeSpan.FromSeconds(30);

    // Maintenance
    options.EnableAutoTableCreation = true;
    options.EnableBackgroundCleanup = true;
    options.CleanupInterval = TimeSpan.FromMinutes(5);
    options.CleanupBatchSize = 1000;
    options.EnableDetailedLogging = false;
});
```

## Usage Examples

### Method Caching
```csharp
public class UserService
{
    [Cache(Duration = "1h", Tags = ["users"])]
    public async Task<User> GetUserAsync(int userId)
    {
        return await _repository.GetUserAsync(userId);
    }

    [Cache(Duration = "30m", Tags = ["users", "profiles"])]
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
// Add health checks
services.AddSqlServerInfrastructureWithHealthChecks(
    configureSqlServer: options =>
    {
        options.ConnectionString = connectionString;
    },
    healthCheckName: "sql_server_cache");

// Configure health check endpoint
app.MapHealthChecks("/health");
```

## Serialization Support

### MessagePack (Recommended)
- Best performance and smallest size
- Binary format, cross-platform compatible
- Requires types to be marked with `[MessagePackObject]`

### JSON
- Human-readable format
- Good for debugging and interoperability
- Slightly larger payload size

### Binary
- Uses System.Text.Json as fallback
- Compatible with most .NET types
- Moderate performance

## Performance Characteristics

### Typical Performance
- **Cache Hit**: 1-5ms (depending on network latency)
- **Cache Miss**: Normal query time + ~2ms overhead
- **Tag Invalidation**: 5-50ms (depending on number of tagged entries)

### Optimization Tips
- Use MessagePack serialization for best performance
- Keep tag names short and meaningful
- Use appropriate connection pooling settings
- Monitor cleanup operations in high-write scenarios

## Best Practices

### 1. Connection String Configuration
```csharp
// Recommended connection string settings
"Server=your-server;Database=your-db;Trusted_Connection=true;
MultipleActiveResultSets=true;Connection Timeout=15;Command Timeout=30;"
```

### 2. Schema Design
- Use a dedicated schema (e.g., `cache`) for isolation
- Grant appropriate permissions to the application user
- Consider partitioning for very large datasets

### 3. Tag Strategy
```csharp
// Good tag design
[Cache(Tags = ["user:123", "department:sales", "role:admin"])]

// Avoid overly broad tags
[Cache(Tags = ["data"])] // Too broad - will invalidate everything
```

### 4. Error Handling
```csharp
// The provider includes automatic retry policies
// Configure circuit breaker for graceful degradation
services.Configure<SqlServerOptions>(options =>
{
    options.CircuitBreakerFailureRatio = 0.5; // 50% failure rate
    options.CircuitBreakerBreakDuration = TimeSpan.FromSeconds(30);
});
```

## Troubleshooting

### Common Issues

#### Connection Failures
- Verify connection string and SQL Server accessibility
- Check firewall and network connectivity
- Ensure SQL Server allows remote connections

#### Table Creation Errors
- Verify database permissions (DDL rights required)
- Check if schema exists and is accessible
- Review SQL Server error logs for detailed messages

#### Performance Issues
- Monitor SQL Server performance counters
- Check for missing indexes on custom queries
- Review cleanup job frequency and batch sizes

#### Tag Invalidation Not Working
- Verify foreign key constraints are properly set up
- Check for transaction isolation issues
- Monitor for deadlocks in high-concurrency scenarios

### Logging
Enable detailed logging for troubleshooting:

```csharp
services.Configure<SqlServerOptions>(options =>
{
    options.EnableDetailedLogging = true;
});
```

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
// Before (In-Memory)
services.AddMethodCache();

// After (SQL Server with L1+L2)
services.AddHybridSqlServerCache(connectionString, options =>
{
    options.L1DefaultExpiration = TimeSpan.FromMinutes(5);
    options.L2DefaultExpiration = TimeSpan.FromHours(4);
});
```

## License

MIT License - see [LICENSE](LICENSE) file for details.

## Contributing

1. Fork the repository
2. Create a feature branch
3. Add tests for new functionality
4. Ensure all tests pass
5. Submit a pull request

## Support

- 📚 [Documentation](https://github.com/methodcache/methodcache/wiki)
- 🐛 [Issues](https://github.com/methodcache/methodcache/issues)
- 💬 [Discussions](https://github.com/methodcache/methodcache/discussions)