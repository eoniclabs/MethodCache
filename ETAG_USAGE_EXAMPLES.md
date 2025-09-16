# ETag Usage Examples

This document demonstrates how to use the comprehensive ETag functionality in MethodCache.

## Basic Setup

```csharp
// Program.cs
using MethodCache.Core;
using MethodCache.HybridCache;
using MethodCache.ETags.Extensions;
using MethodCache.ETags.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Add MethodCache with hybrid L1/L2 caching
builder.Services.AddMethodCache()
    .AddHybridCache(options => {
        options.L1CacheSize = 1000;
        options.L2ConnectionString = "localhost:6379";
    })
    .AddETagSupport(options => {
        // Configure ETag middleware
        options.CacheableContentTypes = new[] { "application/json", "text/html" };
        options.MaxResponseBodySize = 10 * 1024 * 1024; // 10MB limit
        options.DefaultCacheMaxAge = TimeSpan.FromHours(1);
        
        // Add user-specific caching
        options.PersonalizationHeaders = new[] { "Authorization", "X-Tenant-Id" };
        
        // Custom key personalization
        options.KeyPersonalizer = context => {
            var userId = context.User?.FindFirst("sub")?.Value;
            var tenantId = context.Request.Headers["X-Tenant-Id"].FirstOrDefault();
            return $"{userId}:{tenantId}";
        };
    });

var app = builder.Build();

// Add ETag middleware to pipeline
app.UseETagMiddleware();
app.MapControllers();

app.Run();
```

## Controller Examples

```csharp
[ApiController]
[Route("api/[controller]")]
public class DataController : ControllerBase
{
    private readonly IDataService _dataService;
    
    public DataController(IDataService dataService)
    {
        _dataService = dataService;
    }
    
    // Automatic ETag handling via middleware
    [HttpGet("{id}")]
    public async Task<IActionResult> GetData(int id)
    {
        var data = await _dataService.GetDataAsync(id);
        return Ok(data);
    }
    
    // Conditional request handling
    [HttpGet("{id}/detailed")]
    public async Task<IActionResult> GetDetailedData(int id)
    {
        // Check If-None-Match header manually if needed
        var ifNoneMatch = Request.Headers.IfNoneMatch.FirstOrDefault();
        var data = await _dataService.GetDetailedDataAsync(id, ifNoneMatch);
        
        if (data.IsNotModified)
        {
            return StatusCode(304); // Not Modified
        }
        
        Response.Headers.ETag = $"\"{data.ETag}\"";
        return Ok(data.Value);
    }
}
```

## Service Layer with Method Caching

```csharp
public interface IDataService
{
    [Cache("data", Duration = "01:00:00")] // 1 hour cache
    Task<DataModel> GetDataAsync(int id);
    
    [Cache("detailed-data", Duration = "00:30:00", Tags = new[] { "data", "detailed" })]
    Task<DetailedDataModel> GetDetailedDataAsync(int id, string? ifNoneMatch = null);
    
    [CacheInvalidate(Tags = new[] { "data", "detailed" })]
    Task UpdateDataAsync(int id, DataUpdateModel update);
}

public class DataService : IDataService
{
    private readonly IRepository _repository;
    private readonly IETagCacheManager _etagCache;
    
    public DataService(IRepository repository, IETagCacheManager etagCache)
    {
        _repository = repository;
        _etagCache = etagCache;
    }
    
    // Method-level caching with automatic ETag generation
    public async Task<DataModel> GetDataAsync(int id)
    {
        return await _repository.GetDataAsync(id);
    }
    
    // Manual ETag handling for complex scenarios
    public async Task<DetailedDataModel> GetDetailedDataAsync(int id, string? ifNoneMatch = null)
    {
        var cacheKey = $"detailed-data:{id}";
        
        var result = await _etagCache.GetOrCreateWithETagAsync(
            cacheKey,
            async currentETag => 
            {
                var data = await _repository.GetDetailedDataAsync(id);
                var newETag = ETagUtilities.GenerateETag(data);
                
                // Return not modified if ETag matches
                if (currentETag == newETag && currentETag == ifNoneMatch)
                {
                    return ETagCacheEntry<DetailedDataModel>.NotModified(currentETag);
                }
                
                return ETagCacheEntry<DetailedDataModel>.WithValue(data, newETag);
            },
            ifNoneMatch,
            new CacheMethodSettings 
            { 
                Duration = TimeSpan.FromMinutes(30),
                Tags = new List<string> { "data", "detailed" }
            });
        
        return new DetailedDataResult
        {
            Value = result.Value,
            ETag = result.ETag,
            IsNotModified = result.ShouldReturn304
        };
    }
    
    public async Task UpdateDataAsync(int id, DataUpdateModel update)
    {
        await _repository.UpdateDataAsync(id, update);
        
        // Manual cache invalidation
        await _etagCache.InvalidateETagAsync($"data:{id}");
        await _etagCache.InvalidateETagAsync($"detailed-data:{id}");
    }
}
```

## Advanced Configuration

### 1. Custom ETag Backplane (Redis)

```csharp
public class RedisETagBackplane : IETagCacheBackplane
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ISubscriber _subscriber;
    
    public event EventHandler<ETagInvalidationEventArgs> ETagInvalidationReceived;
    public event EventHandler<CacheInvalidationEventArgs> InvalidationReceived;
    
    public RedisETagBackplane(IConnectionMultiplexer redis)
    {
        _redis = redis;
        _subscriber = redis.GetSubscriber();
        
        // Subscribe to ETag invalidation messages
        _subscriber.Subscribe("etag:invalidate", OnInvalidationReceived);
    }
    
    public async Task PublishETagInvalidationAsync(string key, string? newETag = null)
    {
        var message = JsonSerializer.Serialize(new { Key = key, NewETag = newETag });
        await _subscriber.PublishAsync("etag:invalidate", message);
    }
    
    public async Task PublishETagInvalidationBatchAsync(IEnumerable<KeyValuePair<string, string?>> invalidations)
    {
        var message = JsonSerializer.Serialize(invalidations);
        await _subscriber.PublishAsync("etag:invalidate:batch", message);
    }
    
    private void OnInvalidationReceived(RedisChannel channel, RedisValue message)
    {
        var data = JsonSerializer.Deserialize<dynamic>(message);
        ETagInvalidationReceived?.Invoke(this, new ETagInvalidationEventArgs(
            data.Key, data.NewETag));
    }
    
    // Implement other ICacheBackplane members...
}

// Register in DI
builder.Services.AddStackExchangeRedisCache(options => {
    options.Configuration = "localhost:6379";
});
builder.Services.AddSingleton<IETagCacheBackplane, RedisETagBackplane>();
```

### 2. Multi-Tenant ETag Caching

```csharp
// Configure tenant-aware caching
builder.Services.AddETagSupport(options => {
    options.KeyPersonalizer = context => {
        var tenantId = context.Request.Headers["X-Tenant-Id"].FirstOrDefault();
        var userId = context.User?.FindFirst("sub")?.Value;
        return $"tenant:{tenantId}:user:{userId}";
    };
    
    options.PersonalizationHeaders = new[] { "X-Tenant-Id", "Authorization" };
    options.HeadersToIncludeInETag = new[] { "X-Tenant-Version" };
});

[ApiController]
public class TenantDataController : ControllerBase
{
    [HttpGet("data")]
    public async Task<IActionResult> GetTenantData()
    {
        var tenantId = Request.Headers["X-Tenant-Id"].FirstOrDefault();
        var data = await _service.GetTenantDataAsync(tenantId);
        
        // ETag automatically includes tenant context
        return Ok(data);
    }
}
```

### 3. Cache-Control Integration

```csharp
[HttpGet("data")]
public async Task<IActionResult> GetDataWithCacheControl()
{
    // Client can send Cache-Control: no-cache to force revalidation
    // Middleware will still check ETags but bypass cache lookup
    
    var data = await _service.GetDataAsync();
    
    // Set cache directives
    Response.Headers.CacheControl = "public, max-age=3600, must-revalidate";
    return Ok(data);
}
```

## Testing Examples

```csharp
[TestClass]
public class ETagIntegrationTests
{
    private WebApplicationFactory<Program> _factory;
    private HttpClient _client;
    
    [TestInitialize]
    public void Setup()
    {
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }
    
    [TestMethod]
    public async Task Get_Data_Should_Return_ETag()
    {
        // First request
        var response1 = await _client.GetAsync("/api/data/1");
        response1.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var etag = response1.Headers.ETag?.Tag;
        etag.Should().NotBeNullOrEmpty();
        
        // Second request with If-None-Match
        _client.DefaultRequestHeaders.IfNoneMatch.Add(response1.Headers.ETag);
        var response2 = await _client.GetAsync("/api/data/1");
        
        response2.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }
    
    [TestMethod]
    public async Task Cache_Control_No_Cache_Should_Revalidate()
    {
        _client.DefaultRequestHeaders.CacheControl = 
            new CacheControlHeaderValue { NoCache = true };
        
        var response = await _client.GetAsync("/api/data/1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        // Should have fresh ETag even with no-cache
        response.Headers.ETag.Should().NotBeNull();
    }
}
```

## Performance Monitoring

```csharp
public class ETagMetricsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IMetrics _metrics;
    
    public ETagMetricsMiddleware(RequestDelegate next, IMetrics metrics)
    {
        _next = next;
        _metrics = metrics;
    }
    
    public async Task InvokeAsync(HttpContext context)
    {
        var sw = Stopwatch.StartNew();
        
        await _next(context);
        
        sw.Stop();
        
        // Track ETag performance metrics
        var hasETag = context.Response.Headers.ContainsKey("ETag");
        var is304 = context.Response.StatusCode == 304;
        
        _metrics.CreateCounter("etag_requests_total")
            .WithTag("has_etag", hasETag.ToString())
            .WithTag("status_code", context.Response.StatusCode.ToString())
            .Increment();
            
        if (is304)
        {
            _metrics.CreateCounter("etag_304_responses_total").Increment();
        }
        
        _metrics.CreateHistogram("etag_request_duration_ms")
            .Record(sw.ElapsedMilliseconds);
    }
}

// Register metrics middleware
app.UseMiddleware<ETagMetricsMiddleware>();
```

## Key Features Summary

✅ **HTTP Compliance**: RFC-compliant ETag generation and validation  
✅ **Multiple ETag Support**: Handles multiple ETags in If-None-Match headers  
✅ **Security**: Constant-time ETag comparison prevents timing attacks  
✅ **Performance**: Stampede prevention, response buffering limits  
✅ **Scalability**: Distributed cache backplane support  
✅ **Multi-tenancy**: User/tenant-aware cache key personalization  
✅ **Flexibility**: Middleware + method-level caching integration  
✅ **Monitoring**: Built-in logging and metrics support  

The ETag system is now production-ready with comprehensive caching, security, and scalability features!