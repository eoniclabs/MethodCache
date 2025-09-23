# MethodCache.HttpCaching

Standards-compliant HTTP caching for `HttpClient` that implements RFC 7234 caching semantics.

## Features

- **Automatic conditional requests** - Adds `If-None-Match` and `If-Modified-Since` headers
- **304 Not Modified handling** - Returns cached content when server responds with 304
- **Cache-Control compliance** - Respects `max-age`, `no-cache`, `no-store`, `private`, etc.
- **Vary header support** - Caches different responses based on varying headers
- **Freshness calculation** - Implements standard HTTP freshness rules including heuristic freshness
- **Storage abstraction** - Support for in-memory, Redis, or custom cache storage
- **Stale-while-revalidate** - Serve stale content while fetching fresh in background
- **Stale-if-error** - Serve stale content when origin server returns errors
- **Diagnostic headers** - Optional cache debugging information

## Quick Start

### Basic Usage

```csharp
using MethodCache.HttpCaching.Extensions;

// Add HTTP caching to any HttpClient
services.AddHttpClient<IApiClient, ApiClient>()
    .AddHttpCaching();
```

### API-Optimized Configuration

```csharp
services.AddHttpClient<IMetaApiClient, MetaApiClient>()
    .AddApiCaching(options =>
    {
        options.DefaultMaxAge = TimeSpan.FromMinutes(5);
        options.EnableStaleWhileRevalidate = true;
        options.EnableStaleIfError = true;
    });
```

### Development with Debug Headers

```csharp
services.AddHttpClient<IApiClient, ApiClient>()
    .AddHttpCachingWithDebug(options =>
    {
        options.MaxResponseSize = 50 * 1024 * 1024; // 50MB
    });
```

### Custom Storage

```csharp
services.AddHttpClient<IApiClient, ApiClient>()
    .AddHttpCaching(options =>
    {
        options.Storage = new RedisHttpCacheStorage(connectionString);
        options.IsSharedCache = true; // For distributed scenarios
    });
```

## How It Works

The HTTP cache handler sits in the HttpClient pipeline and:

1. **Checks cache** for fresh responses first
2. **Adds conditional headers** (`If-None-Match`, `If-Modified-Since`) for validation
3. **Handles 304 responses** by returning cached content with updated headers
4. **Stores cacheable responses** according to HTTP caching rules
5. **Provides fallbacks** with stale-while-revalidate and stale-if-error

## Benefits for API Integration

### Quota Management
```csharp
// Before: Every call hits the API
GET /api/users/123 → 200 OK (counts against quota)
GET /api/users/123 → 200 OK (counts against quota)
GET /api/users/123 → 200 OK (counts against quota)

// After: Smart HTTP caching
GET /api/users/123 → 200 OK (counts against quota, cached with ETag)
GET /api/users/123 + If-None-Match → 304 Not Modified (minimal quota impact)
GET /api/users/123 + If-None-Match → 304 Not Modified (minimal quota impact)
```

### Bandwidth Savings
- 304 responses have no body content
- Significant bandwidth reduction for large responses
- Automatic cache invalidation when content changes

### Improved Performance
- Sub-millisecond response times for cached content
- Background revalidation keeps cache fresh
- Graceful degradation during API failures

## Diagnostic Headers

When `AddDiagnosticHeaders = true`:

```http
X-Cache: HIT                 # Response served from cache
X-Cache-Age: 120            # Age of cached entry in seconds
X-Cache-TTL: 180            # Time to live remaining in seconds
```

Cache status values:
- `FRESH` - Served from cache, still fresh
- `REVALIDATED` - 304 response, cache updated
- `STALE-WHILE-REVALIDATE` - Stale content served while revalidating
- `STALE-IF-ERROR` - Stale content served due to error
- `MISS` - No cache entry, response from origin

## Testing

The library includes comprehensive test coverage:

### Unit Tests (`MethodCache.HttpCaching.UnitTests`)
- `HttpCacheHandlerTests` - Core handler behavior testing
- Individual component testing with mocked dependencies
- Fast-running tests focused on business logic

### Integration Tests (`MethodCache.HttpCaching.IntegrationTests`)
- `HttpCacheIntegrationTests` - DI container integration
- `HttpCacheEndToEndTests` - Full pipeline testing
- `HttpCacheStorageTests` - Storage implementation testing

Run tests:
```bash
# Unit tests
dotnet test MethodCache.HttpCaching.UnitTests/

# Integration tests
dotnet test MethodCache.HttpCaching.IntegrationTests/

# All HTTP caching tests
dotnet test --filter "FullyQualifiedName~HttpCaching"
```

## Configuration Options

```csharp
public class HttpCacheOptions
{
    // Standard HTTP compliance
    public bool RespectCacheControl { get; set; } = true;
    public bool RespectVary { get; set; } = true;
    public bool IsSharedCache { get; set; } = false;

    // Freshness calculation
    public bool AllowHeuristicFreshness { get; set; } = true;
    public TimeSpan MaxHeuristicFreshness { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan? DefaultMaxAge { get; set; } = TimeSpan.FromMinutes(5);

    // Cacheable methods
    public HashSet<HttpMethod> CacheableMethods { get; set; } = { GET, HEAD, OPTIONS };

    // Size limits
    public long MaxCacheSize { get; set; } = 100 * 1024 * 1024; // 100MB
    public long MaxResponseSize { get; set; } = 10 * 1024 * 1024; // 10MB

    // Advanced features
    public bool EnableStaleWhileRevalidate { get; set; } = false;
    public bool EnableStaleIfError { get; set; } = false;
    public TimeSpan MaxStaleIfError { get; set; } = TimeSpan.FromHours(24);

    // Debugging
    public bool AddDiagnosticHeaders { get; set; } = false;
}
```

## Storage Implementations

### In-Memory (Default)
- Uses `IMemoryCache`
- Perfect for single-instance applications
- Automatic memory management and eviction

### Redis (Available)
- Distributed caching across multiple instances
- Persistent across application restarts
- Ideal for scaled applications

### Custom Storage
Implement `IHttpCacheStorage` for custom backends:

```csharp
public class CustomHttpCacheStorage : IHttpCacheStorage
{
    public Task<HttpCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        // Your implementation
    }

    public Task SetAsync(string key, HttpCacheEntry entry, CancellationToken cancellationToken = default)
    {
        // Your implementation
    }

    // ... other methods
}
```

## Integration with Resilience

HTTP caching works **alongside** existing resilience patterns:

```csharp
services.AddHttpClient<IMetaApiClient, MetaApiClient>()
    .AddPolicyHandler(GetRetryPolicy())           // Your retry logic
    .AddPolicyHandler(GetRateLimitPolicy())       // Your rate limiting
    .AddPolicyHandler(GetCircuitBreakerPolicy())  // Your circuit breaker
    .AddHttpCaching();                            // HTTP caching (add last)
```

The cache handler only manages HTTP-level caching and doesn't interfere with your domain-specific resilience strategies.