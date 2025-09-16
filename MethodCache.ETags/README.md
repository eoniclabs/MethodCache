# MethodCache.ETags

[![NuGet](https://img.shields.io/nuget/v/MethodCache.ETags.svg)](https://www.nuget.org/packages/MethodCache.ETags)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

HTTP ETag caching support for MethodCache with hybrid L1/L2 cache integration. Enables conditional HTTP caching and cross-instance ETag consistency for optimal performance and bandwidth usage.

## Features

- 🚀 **Hybrid Cache Integration**: Leverages MethodCache's L1/L2 architecture for optimal ETag performance
- 🌐 **HTTP Standards Compliant**: Full support for ETags, If-None-Match, and 304 Not Modified responses
- 📡 **Cross-Instance Consistency**: ETag invalidation across multiple application instances
- ⚡ **High Performance**: L1 cache for fast ETag validation, L2 for distributed consistency
- 🎯 **Flexible Configuration**: Middleware and attribute-based configuration options
- 🛡️ **Production Ready**: Comprehensive error handling, logging, and monitoring support

## Quick Start

### 1. Installation

```bash
dotnet add package MethodCache.ETags
```

### 2. Basic Configuration

```csharp
// Startup.cs or Program.cs
using MethodCache.ETags.Extensions;
using MethodCache.HybridCache.Extensions;

// Configure services
services.AddHybridCache<RedisCacheManager>()  // Your L2 cache
    .AddETagSupport(options =>
    {
        options.DefaultExpiration = TimeSpan.FromHours(24);
        options.CacheableContentTypes = new[] { "application/json", "text/html" };
    });

// Configure pipeline
app.UseETagCaching();  // Add early in pipeline
```

### 3. Method-Level ETag Caching

```csharp
using MethodCache.ETags.Attributes;

public class UserService
{
    [Cache(Duration = "1h")]
    [ETag]  // Enable ETag support
    public async Task<UserProfile> GetUserProfile(int userId)
    {
        return await _repository.GetUserAsync(userId);
    }

    [Cache(Duration = "30m")]
    [ETag(Strategy = ETagGenerationStrategy.LastModified)]
    public async Task<List<Product>> GetProducts()
    {
        return await _repository.GetProductsAsync();
    }
}
```

### 4. Programmatic ETag Usage

```csharp
public class ApiController : ControllerBase
{
    private readonly IETagCacheManager _etagCache;

    [HttpGet("users/{id}")]
    public async Task<IActionResult> GetUser(int id)
    {
        var ifNoneMatch = Request.Headers.IfNoneMatch.ToString();
        
        var result = await _etagCache.GetOrCreateWithETagAsync(
            $"user:{id}",
            async () => ETagCacheEntry<UserDto>.WithValue(
                await _userService.GetUserAsync(id),
                GenerateUserETag(id)),
            ifNoneMatch);

        if (result.ShouldReturn304)
        {
            return StatusCode(304);
        }

        Response.Headers.ETag = result.ETag;
        return Ok(result.Value);
    }
}
```

## Architecture

### ETag + Hybrid Cache Flow

```
┌─────────────┐    ┌──────────────┐    ┌──────────────┐
│   Client    │    │      L1      │    │      L2      │
│             │    │ (In-Memory)  │    │ (Distributed)│
└─────────────┘    └──────────────┘    └──────────────┘
       │                   │                   │
       │ If-None-Match     │                   │
       ├──────────────────►│                   │
       │                   │ ETag Check        │
       │                   ├──────────────────►│
       │                   │                   │
       │ 304 Not Modified  │                   │
       ◄──────────────────┤                   │
       │                   │                   │
```

### Cache Layers

1. **L1 Cache (In-Memory)**
   - Fastest ETag validation
   - Immediate response for matching ETags
   - Automatic warming from L2

2. **L2 Cache (Distributed)**
   - Cross-instance ETag consistency
   - Persistent ETag storage
   - Backplane invalidation support

3. **HTTP Layer**
   - Standard ETag headers
   - 304 Not Modified responses
   - Content-Type aware caching

## Configuration

### Middleware Options

```csharp
services.AddETagSupport(options =>
{
    // Basic settings
    options.DefaultExpiration = TimeSpan.FromHours(24);
    options.DefaultCacheMaxAge = TimeSpan.FromHours(1);
    
    // Content filtering
    options.CacheableContentTypes = new[] 
    {
        "application/json",
        "text/html",
        "text/css",
        "application/javascript"
    };
    
    // Path filtering
    options.SkipPaths = new[] 
    {
        "/api/admin",
        "/health",
        "/metrics"
    };
    
    // Key generation
    options.IncludeQueryStringInKey = true;
    options.HeadersToIncludeInKey = new[] { "Accept", "Accept-Language" };
    
    // ETag generation
    options.HeadersToIncludeInETag = new[] { "Content-Type" };
    
    // Response caching
    options.HeadersToCache = new[] { "Content-Type", "X-Custom-Header" };
    options.AddCacheControlHeader = true;
    options.AddLastModifiedHeader = true;
});
```

### ETag Attribute Configuration

```csharp
public class ProductService
{
    // Content-based ETag (default)
    [Cache(Duration = "1h")]
    [ETag]
    public async Task<Product> GetProduct(int id) { /* ... */ }

    // Timestamp-based ETag
    [Cache(Duration = "30m")]
    [ETag(Strategy = ETagGenerationStrategy.LastModified)]
    public async Task<List<Product>> GetProducts() { /* ... */ }

    // Version-based ETag
    [Cache(Duration = "2h")]
    [ETag(Strategy = ETagGenerationStrategy.Version)]
    public async Task<CatalogData> GetCatalog() { /* ... */ }

    // Custom ETag generator
    [Cache(Duration = "45m")]
    [ETag(Strategy = ETagGenerationStrategy.Custom, 
          ETagGeneratorType = typeof(CustomProductETagGenerator))]
    public async Task<ProductDetails> GetProductDetails(int id) { /* ... */ }

    // Weak ETag with metadata
    [Cache(Duration = "15m")]
    [ETag(UseWeakETag = true, 
          Metadata = new[] { "version:2", "format:json" })]
    public async Task<ApiResponse> GetApiData() { /* ... */ }
}
```

### Custom ETag Generator

```csharp
public class CustomProductETagGenerator : IETagGenerator
{
    public async Task<string> GenerateETagAsync(object content, ETagGenerationContext context)
    {
        if (content is Product product)
        {
            // Combine product data with last modified timestamp
            var combined = $"{product.Id}:{product.LastModified.Ticks}:{product.Version}";
            return ETagUtilities.GenerateETag(combined, context.UseWeakETag);
        }

        // Fallback to default content-based ETag
        return ETagUtilities.GenerateETag(content, context.UseWeakETag);
    }
}
```

## Advanced Usage

### Cross-Instance ETag Invalidation

```csharp
// Service registration
services.AddHybridCache<RedisCacheManager>()
    .AddETagSupport()
    .AddETagBackplane<RedisETagBackplane>();

// Programmatic invalidation
public class ProductService
{
    private readonly IETagCacheManager _etagCache;

    public async Task UpdateProduct(Product product)
    {
        await _repository.UpdateAsync(product);
        
        // Invalidate across all instances
        await _etagCache.InvalidateETagAsync($"product:{product.Id}");
    }

    public async Task BulkUpdateProducts(List<Product> products)
    {
        await _repository.BulkUpdateAsync(products);
        
        // Batch invalidation
        var keys = products.Select(p => $"product:{p.Id}").ToArray();
        await _etagCache.InvalidateETagsAsync(keys);
    }
}
```

### Conditional Processing

```csharp
public class DataController : ControllerBase
{
    [HttpGet("report/{id}")]
    public async Task<IActionResult> GetReport(int id)
    {
        var ifNoneMatch = Request.Headers.IfNoneMatch.ToString();
        
        var result = await _etagCache.GetOrCreateWithETagAsync(
            $"report:{id}",
            async currentETag =>
            {
                // Check if report has changed
                var reportVersion = await _reportService.GetVersionAsync(id);
                var versionETag = ETagUtilities.GenerateETagFromVersion(reportVersion);
                
                if (versionETag == currentETag)
                {
                    // Report hasn't changed
                    return ETagCacheEntry<Report>.NotModified(versionETag);
                }
                
                // Generate fresh report
                var report = await _reportService.GenerateReportAsync(id);
                return ETagCacheEntry<Report>.WithValue(report, versionETag);
            },
            ifNoneMatch);

        if (result.ShouldReturn304)
        {
            return StatusCode(304);
        }

        Response.Headers.ETag = result.ETag;
        Response.Headers.LastModified = result.LastModified.ToString("R");
        
        return Ok(result.Value);
    }
}
```

### Pipeline Integration

```csharp
public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
{
    // Early pipeline
    app.UseAuthentication();
    app.UseAuthorization();
    
    // ETag middleware (after auth, before MVC)
    app.UseETagCaching();
    
    // Content generation
    app.UseRouting();
    app.UseEndpoints(endpoints =>
    {
        endpoints.MapControllers();
    });
}

// Environment-specific configuration
app.UseETagCachingInEnvironments("Production", "Staging");

// Conditional usage
app.UseETagCachingWhen(() => 
    Configuration.GetValue<bool>("Features:ETagCaching"));
```

## Performance Characteristics

### Cache Hit Performance

- **L1 ETag Hit**: ~0.1ms (in-memory lookup)
- **L2 ETag Hit**: ~1-5ms (distributed cache + L1 warming)
- **ETag Miss**: Normal factory execution + caching overhead

### Memory Usage

- **ETag Storage**: ~100 bytes per cached entry (key + ETag)
- **HTTP Response**: ~2KB per cached response (headers + body)
- **L1 Cache**: Configurable limits with LRU eviction

### Network Bandwidth Savings

- **304 Responses**: ~200 bytes vs full response
- **Typical Savings**: 80-95% for cacheable content
- **Cross-Instance**: Reduced L2 cache traffic

## Monitoring and Observability

### Built-in Logging

```csharp
// Enable debug logging
services.Configure<ETagMiddlewareOptions>(options =>
{
    options.EnableDebugLogging = true;
});

// Log outputs:
// [Debug] ETag match in L1 cache for key user:123: "abc123"
// [Debug] Cache hit in L2 for key product:456, warmed L1
// [Debug] Returning 304 Not Modified for ETag "def456"
```

### Custom Metrics

```csharp
public class ETagMetricsService : IETagMetrics
{
    public void RecordCacheHit(string key, string layer) { /* ... */ }
    public void RecordCacheMiss(string key) { /* ... */ }
    public void Record304Response(string key) { /* ... */ }
}

// Register in DI
services.AddSingleton<IETagMetrics, ETagMetricsService>();
```

## Best Practices

### 1. ETag Strategy Selection

- **Content Hash**: Best for dynamic content that changes unpredictably
- **Last Modified**: Ideal for data with reliable timestamps
- **Version**: Perfect for versioned APIs and resources
- **Custom**: Use when you need specific business logic

### 2. Cache Duration Guidelines

```csharp
// Static resources
[ETag(CacheDurationMinutes = 1440)]  // 24 hours

// User-specific data  
[ETag(CacheDurationMinutes = 60)]    // 1 hour

// Frequently changing data
[ETag(CacheDurationMinutes = 15)]    // 15 minutes

// Real-time data
[ETag(CacheDurationMinutes = 1)]     // 1 minute
```

### 3. Key Design Patterns

```csharp
// Include version in key for breaking changes
$"api:v2:users:{userId}"

// Include user context for personalized content
$"dashboard:{userId}:summary"

// Include relevant parameters
$"search:{query}:{page}:{pageSize}"

// Use hierarchical invalidation
$"tenant:{tenantId}:products"
```

### 4. Error Handling

```csharp
public async Task<IActionResult> GetData(int id)
{
    try
    {
        var result = await _etagCache.GetOrCreateWithETagAsync(/* ... */);
        return HandleETagResult(result);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "ETag cache error for id {Id}", id);
        // Fallback to non-cached response
        return Ok(await _service.GetDataAsync(id));
    }
}
```

## Troubleshooting

### Common Issues

#### 1. ETags Not Being Generated
- Verify `IETagCacheManager` is registered
- Check content type eligibility
- Ensure paths aren't in skip list

#### 2. 304 Responses Not Working
- Validate ETag format (quoted strings)
- Check If-None-Match header parsing
- Verify client ETag handling

#### 3. Cross-Instance Inconsistency
- Confirm backplane is configured
- Check Redis connectivity
- Verify instance IDs are unique

#### 4. Performance Issues
- Monitor L1 cache hit ratios
- Check L2 cache latency
- Review ETag generation complexity

### Debug Mode

```csharp
services.Configure<ETagMiddlewareOptions>(options =>
{
    options.EnableDetailedLogging = true;
    options.LogETagGeneration = true;
    options.LogCacheOperations = true;
});
```

## Migration Guide

### From HTTP Response Caching

```csharp
// Before
app.UseResponseCaching();
[ResponseCache(Duration = 3600)]

// After  
app.UseETagCaching();
[Cache(Duration = "1h")]
[ETag]
```

### From Custom ETag Implementation

```csharp
// Before
Response.Headers.ETag = GenerateETag(data);
if (Request.Headers.IfNoneMatch == etag)
    return StatusCode(304);

// After
var result = await _etagCache.GetOrCreateWithETagAsync(
    key, () => ETagCacheEntry<T>.WithValue(data, etag), ifNoneMatch);
return result.ShouldReturn304 ? StatusCode(304) : Ok(result.Value);
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
- 📧 [Email Support](mailto:support@methodcache.com)