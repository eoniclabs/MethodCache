# MethodCache.HttpCaching

Standards-compliant HTTP caching for `HttpClient` that implements RFC 7234 caching semantics.

## Features

- **Automatic conditional requests** - Adds `If-None-Match` and `If-Modified-Since` headers
- **304 Not Modified handling** - Returns cached content when server responds with 304
- **Cache-Control compliance** - Respects `max-age`, `no-cache`, `no-store`, `private`, etc.
- **Vary header support** - Caches different responses based on varying headers
- **Freshness calculation** - Implements standard HTTP freshness rules including heuristic freshness
- **Storage abstraction** - In-memory storage by default, or your own `IHttpCacheStorage`
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

### Configuration

```csharp
services.AddHttpClient<IMetaApiClient, MetaApiClient>()
    .AddHttpCaching(caching => caching
        .Configure(options =>
        {
            options.Freshness.DefaultMaxAge = TimeSpan.FromMinutes(5);
            options.Behavior.EnableStaleWhileRevalidate = true;
            options.Behavior.EnableStaleIfError = true;
            options.Diagnostics.AddDiagnosticHeaders = true; // X-Cache headers, useful in development
        })
        .UseInMemoryStorage(storage => storage.MaxResponseSize = 50 * 1024 * 1024)); // 50MB
```

### Custom Storage

```csharp
services.AddHttpClient<IApiClient, ApiClient>()
    .AddHttpCaching(caching => caching.UseCustomStorage<MyHttpCacheStorage>());
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

When `options.Diagnostics.AddDiagnosticHeaders = true`:

```http
X-Cache: HIT                 # Response served from cache
X-Cache-Age: 120             # Age of cached entry in seconds
X-Cache-TTL: 180             # Time to live remaining in seconds
```

Cache status values:
- `HIT` - Served from cache, still fresh
- `REVALIDATED` - 304 response, cache updated
- `STALE` - Stale content served while revalidating in the background
- `STALE-IF-ERROR` - Stale content served due to error
- `MISS` - No cache entry, response from origin

## Testing

```bash
dotnet test MethodCache.HttpCaching.Tests/
dotnet test MethodCache.HttpCaching.IntegrationTests/
```

## Configuration Options

`HttpCacheOptions` groups its settings (defaults shown):

```csharp
options.Behavior.RespectCacheControl = true;
options.Behavior.RespectVary = true;
options.Behavior.IsSharedCache = false;            // true for a cache shared between users (honors s-maxage)
options.Behavior.EnableStaleWhileRevalidate = false;
options.Behavior.EnableStaleIfError = false;
options.Behavior.MaxStaleIfError = TimeSpan.FromHours(24);

options.Freshness.AllowHeuristicFreshness = true;
options.Freshness.MaxHeuristicFreshness = TimeSpan.FromHours(1);
options.Freshness.DefaultMaxAge = TimeSpan.FromMinutes(5);

options.CacheableMethods;                          // GET, HEAD and OPTIONS by default
options.CacheKeyGenerator = request => ...;        // optional custom key
options.Diagnostics.AddDiagnosticHeaders = false;
```

Size limits are on the storage options: `MaxCacheSize` (100MB) and `MaxResponseSize` (10MB), set with `ConfigureStorage(...)` or `UseInMemoryStorage(...)`.

## Storage Implementations

### In-Memory (Default)
- `InMemoryHttpCacheStorage`, built on `IMemoryCache`
- For single-instance applications

### Custom Storage
Implement `IHttpCacheStorage` (`GetAsync`, `SetAsync`, `RemoveAsync`, `ClearAsync`) and register it with `UseCustomStorage<T>()`:

```csharp
public class MyHttpCacheStorage : IHttpCacheStorage
{
    public ValueTask<HttpCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default) { ... }
    public ValueTask SetAsync(string key, HttpCacheEntry entry, CancellationToken cancellationToken = default) { ... }
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) { ... }
    public ValueTask ClearAsync(CancellationToken cancellationToken = default) { ... }
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