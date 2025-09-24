# Third-Party Library Caching Guide

## Overview

MethodCache's runtime configuration system allows you to add caching behavior to third-party libraries without modifying their source code. This is incredibly powerful for:

- **Reducing API costs** - Cache expensive external API calls
- **Improving performance** - Avoid redundant database queries
- **Enhancing reliability** - Reduce dependency on external services
- **Controlling behavior** - Manage third-party library performance from your code

## How It Works

Since runtime configuration has the **highest priority (40)**, it can override all other configuration sources. This means you can:

1. **Configure caching for any interface-based library** via JSON/YAML configuration
2. **Control caching behavior** through your management interface
3. **Emergency disable** problematic caches instantly
4. **A/B test** different caching strategies for external calls

## Supported Library Types

### ✅ **CAN Cache**

| Library Type | Examples | How |
|--------------|----------|-----|
| **Interface-based libraries** | Most modern .NET libraries | Direct configuration |
| **HTTP/REST clients** | RestSharp, Refit, Flurl | Interface interception |
| **Database libraries** | Dapper, EF Core, MongoDB | Repository patterns |
| **Cloud SDKs** | AWS, Azure, Google Cloud | Service interfaces |
| **GraphQL clients** | GraphQL.Client, Hot Chocolate | Query interfaces |
| **Payment gateways** | Stripe, PayPal, Square | Client interfaces |

### ❌ **CANNOT Cache (Directly)**

| Library Type | Why | Workaround |
|--------------|-----|------------|
| **Static methods** | No interception point | Create wrapper interface |
| **Sealed classes** | Cannot inherit | Create adapter pattern |
| **Extension methods** | Static by nature | Wrap in service class |

## Configuration Examples

### Basic Configuration

```json
{
  "MethodCache": {
    "Services": {
      "ExternalLibrary.IExternalService": {
        "Methods": {
          "GetDataAsync": {
            "Duration": "00:05:00",
            "Tags": ["external", "api"],
            "Enabled": true
          }
        }
      }
    }
  }
}
```

### Pattern Matching

```json
{
  "MethodCache": {
    "Services": {
      "*Repository": {  // Matches all repository interfaces
        "Methods": {
          "Get*": {  // Matches all Get methods
            "Duration": "00:10:00",
            "Tags": ["repository", "read"]
          },
          "Save*": {  // Never cache writes
            "Enabled": false
          }
        }
      }
    }
  }
}
```

## Real-World Examples

### 1. Weather API Client

```csharp
// NuGet: WeatherAPI.Client
public interface IWeatherClient
{
    Task<CurrentWeather> GetCurrentAsync(string city);
    Task<Forecast> GetForecastAsync(string city, int days);
    Task<HistoricalData> GetHistoricalAsync(string city, DateTime date);
}
```

```json
{
  "MethodCache": {
    "Services": {
      "WeatherAPI.Client.IWeatherClient": {
        "Methods": {
          "GetCurrentAsync": {
            "Duration": "00:05:00",  // Current weather changes frequently
            "Tags": ["weather", "current"]
          },
          "GetForecastAsync": {
            "Duration": "01:00:00",  // Forecasts update hourly
            "Tags": ["weather", "forecast"]
          },
          "GetHistoricalAsync": {
            "Duration": "24:00:00",  // Historical data never changes
            "Tags": ["weather", "historical"]
          }
        }
      }
    }
  }
}
```

### 2. Stripe Payment Gateway

```csharp
// NuGet: Stripe.net
public interface IStripeClient
{
    Task<Customer> GetCustomerAsync(string customerId);
    Task<List<Invoice>> ListInvoicesAsync(string customerId);
    Task<PaymentIntent> CreatePaymentIntentAsync(PaymentIntentCreateOptions options);
    Task<Subscription> GetSubscriptionAsync(string subscriptionId);
}
```

```json
{
  "MethodCache": {
    "Services": {
      "Stripe.IStripeClient": {
        "Methods": {
          "GetCustomerAsync": {
            "Duration": "00:30:00",  // Customer data is fairly stable
            "Tags": ["stripe", "customer"]
          },
          "ListInvoicesAsync": {
            "Duration": "00:15:00",  // Invoice lists change occasionally
            "Tags": ["stripe", "invoice"]
          },
          "CreatePaymentIntentAsync": {
            "Enabled": false  // NEVER cache payment creation!
          },
          "GetSubscriptionAsync": {
            "Duration": "00:10:00",  // Subscriptions can change
            "Tags": ["stripe", "subscription"]
          }
        }
      }
    }
  }
}
```

### 3. AWS S3 SDK

```csharp
// NuGet: AWSSDK.S3
public interface IAmazonS3
{
    Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request);
    Task<GetObjectMetadataResponse> GetObjectMetadataAsync(string bucket, string key);
    Task<ListObjectsV2Response> ListObjectsV2Async(ListObjectsV2Request request);
    Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request);
    Task<DeleteObjectResponse> DeleteObjectAsync(string bucket, string key);
}
```

```json
{
  "MethodCache": {
    "Services": {
      "Amazon.S3.IAmazonS3": {
        "Methods": {
          "GetObjectAsync": {
            "Duration": "00:30:00",  // Cache downloaded objects
            "Tags": ["s3", "object", "download"]
          },
          "GetObjectMetadataAsync": {
            "Duration": "01:00:00",  // Metadata rarely changes
            "Tags": ["s3", "metadata"]
          },
          "ListObjectsV2Async": {
            "Duration": "00:05:00",  // Lists can change frequently
            "Tags": ["s3", "list"]
          },
          "PutObjectAsync": {
            "Enabled": false  // Never cache uploads
          },
          "DeleteObjectAsync": {
            "Enabled": false  // Never cache deletes
          }
        }
      }
    }
  }
}
```

### 4. Entity Framework Core

```csharp
// Entity Framework repositories
public interface IRepository<T>
{
    Task<T> GetByIdAsync(int id);
    Task<List<T>> GetAllAsync();
    Task<List<T>> GetByFilterAsync(Expression<Func<T, bool>> filter);
    Task<T> AddAsync(T entity);
    Task UpdateAsync(T entity);
    Task DeleteAsync(int id);
}
```

```json
{
  "MethodCache": {
    "Services": {
      "MyApp.Data.IRepository`1": {  // Generic repository pattern
        "Methods": {
          "GetByIdAsync": {
            "Duration": "00:05:00",
            "Tags": ["repository", "by-id"]
          },
          "GetAllAsync": {
            "Duration": "00:01:00",  // Lists change more often
            "Tags": ["repository", "all"]
          },
          "GetByFilterAsync": {
            "Duration": "00:02:00",
            "Tags": ["repository", "filter"]
          },
          "AddAsync": { "Enabled": false },
          "UpdateAsync": { "Enabled": false },
          "DeleteAsync": { "Enabled": false }
        }
      }
    }
  }
}
```

### 5. GraphQL Client

```csharp
// NuGet: GraphQL.Client
public interface IGraphQLClient
{
    Task<GraphQLResponse<T>> SendQueryAsync<T>(GraphQLRequest request);
    Task<GraphQLResponse<T>> SendMutationAsync<T>(GraphQLRequest request);
}
```

```json
{
  "MethodCache": {
    "Services": {
      "GraphQL.Client.IGraphQLClient": {
        "Methods": {
          "SendQueryAsync": {
            "Duration": "00:10:00",
            "Tags": ["graphql", "query"],
            "Condition": "request.Query.Contains('query')"  // Only cache queries
          },
          "SendMutationAsync": {
            "Enabled": false  // Never cache mutations
          }
        }
      }
    }
  }
}
```

## Wrapper Pattern for Non-Interface Libraries

When a third-party library doesn't expose interfaces, create a wrapper:

### Original Static Class
```csharp
// Third-party library with static methods
public static class ThirdPartyHelper
{
    public static async Task<string> ProcessDataAsync(string input)
    {
        // Expensive operation
        return await CallExternalApiAsync(input);
    }
}
```

### Create Wrapper Interface
```csharp
public interface IThirdPartyService
{
    Task<string> ProcessDataAsync(string input);
}

public class ThirdPartyServiceWrapper : IThirdPartyService
{
    public async Task<string> ProcessDataAsync(string input)
    {
        return await ThirdPartyHelper.ProcessDataAsync(input);
    }
}
```

### Configure Caching
```json
{
  "MethodCache": {
    "Services": {
      "MyApp.Services.IThirdPartyService": {
        "Methods": {
          "ProcessDataAsync": {
            "Duration": "00:15:00",
            "Tags": ["third-party", "wrapper"]
          }
        }
      }
    }
  }
}
```

### Register Services
```csharp
services.AddSingleton<IThirdPartyService, ThirdPartyServiceWrapper>();
services.AddMethodCacheWithSources(cache => {
    cache.AddJsonConfiguration(configuration);
    cache.AddRuntimeConfiguration();  // Highest priority for management
});
```

## Management Interface

Build powerful management interfaces to control third-party caching:

```csharp
[ApiController]
[Route("api/admin/external-cache")]
public class ExternalCacheManagementController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExternalCacheManagementController> _logger;
    
    [HttpGet("status")]
    public IActionResult GetExternalCacheStatus()
    {
        var services = _configuration
            .GetSection("MethodCache:Services")
            .GetChildren()
            .Where(s => s.Key.Contains("External") || s.Key.Contains("Client"))
            .Select(s => new
            {
                Service = s.Key,
                Methods = s.GetSection("Methods").GetChildren().Select(m => new
                {
                    Method = m.Key,
                    Duration = m["Duration"],
                    Enabled = m["Enabled"] ?? "true",
                    Tags = m.GetSection("Tags").Get<string[]>()
                })
            });
        
        return Ok(services);
    }
    
    [HttpPost("configure")]
    public async Task<IActionResult> ConfigureExternalCache(
        [FromBody] ExternalCacheConfig request)
    {
        var key = $"MethodCache:Services:{request.ServiceName}:Methods:{request.MethodName}";
        
        // Update configuration (would persist to config store in production)
        _configuration[key + ":Duration"] = request.Duration.ToString();
        _configuration[key + ":Enabled"] = request.Enabled.ToString();
        
        if (request.Tags?.Any() == true)
        {
            for (int i = 0; i < request.Tags.Length; i++)
            {
                _configuration[$"{key}:Tags:{i}"] = request.Tags[i];
            }
        }
        
        _logger.LogInformation(
            "External cache configured: {Service}.{Method} - Duration: {Duration}, Enabled: {Enabled}",
            request.ServiceName, request.MethodName, request.Duration, request.Enabled);
        
        return Ok(new { Message = "Configuration updated", request.ServiceName, request.MethodName });
    }
    
    [HttpPost("invalidate")]
    public async Task<IActionResult> InvalidateExternalCache(
        [FromBody] InvalidateRequest request)
    {
        await _cacheManager.InvalidateByTagsAsync(request.Tags);
        
        _logger.LogInformation("External cache invalidated for tags: {Tags}", 
            string.Join(", ", request.Tags));
        
        return Ok(new { Message = "Cache invalidated", request.Tags });
    }
    
    [HttpPost("emergency-disable")]
    public async Task<IActionResult> EmergencyDisableAllExternal()
    {
        // Disable all external service caching
        var externalServices = new[] 
        {
            "*Client*",
            "*External*",
            "*Api*",
            "Stripe.*",
            "AWS.*",
            "Azure.*"
        };
        
        foreach (var pattern in externalServices)
        {
            _configuration[$"MethodCache:Services:{pattern}:Methods:*:Enabled"] = "false";
        }
        
        _logger.LogWarning("Emergency: All external service caching disabled");
        
        return Ok(new { Message = "All external caching disabled", Patterns = externalServices });
    }
}

public record ExternalCacheConfig(
    string ServiceName,
    string MethodName,
    TimeSpan Duration,
    bool Enabled,
    string[]? Tags);

public record InvalidateRequest(string[] Tags);
```

## Monitoring Third-Party Cache Performance

```csharp
public class ThirdPartyCacheMetrics
{
    private readonly IMetricsCollector _metrics;
    
    public void RecordCacheHit(string service, string method)
    {
        _metrics.Increment("third_party.cache.hit", 
            new Dictionary<string, string>
            {
                ["service"] = service,
                ["method"] = method,
                ["type"] = DetermineServiceType(service)
            });
    }
    
    public void RecordCacheMiss(string service, string method, TimeSpan callDuration)
    {
        _metrics.Increment("third_party.cache.miss",
            new Dictionary<string, string>
            {
                ["service"] = service,
                ["method"] = method,
                ["type"] = DetermineServiceType(service)
            });
        
        _metrics.RecordValue("third_party.call.duration", 
            callDuration.TotalMilliseconds,
            new Dictionary<string, string>
            {
                ["service"] = service,
                ["method"] = method
            });
    }
    
    public void RecordApiSavings(string service, decimal savedApiCalls, decimal estimatedCostSaved)
    {
        _metrics.RecordValue("third_party.api.calls_saved", savedApiCalls,
            new Dictionary<string, string> { ["service"] = service });
        
        _metrics.RecordValue("third_party.api.cost_saved_usd", estimatedCostSaved,
            new Dictionary<string, string> { ["service"] = service });
    }
    
    private string DetermineServiceType(string service)
    {
        return service switch
        {
            var s when s.Contains("Stripe") => "payment",
            var s when s.Contains("AWS") || s.Contains("S3") => "cloud",
            var s when s.Contains("Weather") => "weather",
            var s when s.Contains("Repository") => "database",
            var s when s.Contains("GraphQL") => "graphql",
            _ => "other"
        };
    }
}
```

## Best Practices

### 1. **Start with Short Cache Durations**
```json
{
  "MethodCache": {
    "Services": {
      "NewExternalAPI.IClient": {
        "Methods": {
          "GetDataAsync": {
            "Duration": "00:00:30",  // Start with 30 seconds
            "Tags": ["test", "external"]
          }
        }
      }
    }
  }
}
```

### 2. **Monitor Cache Effectiveness**
- Track hit rates
- Measure API call reduction
- Calculate cost savings
- Monitor data freshness

### 3. **Use Appropriate Tags**
```json
{
  "Tags": [
    "external",        // All external calls
    "stripe",          // Service-specific
    "payment",         // Domain-specific
    "customer-123"     // Entity-specific
  ]
}
```

### 4. **Never Cache Non-Idempotent Operations**
```json
{
  "CreatePaymentAsync": { "Enabled": false },
  "UpdateCustomerAsync": { "Enabled": false },
  "DeleteResourceAsync": { "Enabled": false },
  "ProcessTransactionAsync": { "Enabled": false }
}
```

### 5. **Test Cache Invalidation**
```csharp
[Test]
public async Task ExternalCache_Should_Invalidate_On_Update()
{
    // Arrange
    var client = GetConfiguredClient();
    
    // Act
    var data1 = await client.GetDataAsync("key");  // Cache miss
    var data2 = await client.GetDataAsync("key");  // Cache hit
    
    await InvalidateCache("external");
    
    var data3 = await client.GetDataAsync("key");  // Cache miss after invalidation
    
    // Assert
    Assert.AreEqual(data1, data2);  // Same cached data
    Assert.AreEqual(data1, data3);  // Same data, different cache entry
}
```

## Security Considerations

### 1. **Validate Cache Keys**
Ensure third-party method parameters don't contain sensitive data that would be exposed in cache keys.

### 2. **Audit Configuration Changes**
```csharp
public async Task AuditConfigurationChange(string service, string method, string changedBy)
{
    await _auditLog.LogAsync(new AuditEntry
    {
        Action = "ThirdPartyCacheConfigurationChanged",
        Service = service,
        Method = method,
        User = changedBy,
        Timestamp = DateTime.UtcNow
    });
}
```

### 3. **Rate Limit Management APIs**
Protect management endpoints from abuse:
```csharp
[RateLimit(10, Period = "1m")]  // 10 requests per minute
[Authorize(Roles = "CacheAdmin")]
public async Task<IActionResult> ConfigureCache(...) { }
```

## FAQ

### Q: Can I cache private methods in third-party libraries?
**A:** No, only public methods on interfaces can be cached through configuration.

### Q: What happens if the third-party library throws an exception?
**A:** Exceptions are not cached by default. The cache is bypassed and the exception propagates normally.

### Q: Can I cache generic methods?
**A:** Yes, use the backtick notation: `IRepository\`1` for `IRepository<T>`

### Q: How do I handle version updates of third-party libraries?
**A:** Cache keys include method signatures, so incompatible changes automatically invalidate old cache entries.

### Q: Can I use different cache providers for different third-party libraries?
**A:** Yes, configure different providers per service in your configuration.

## Summary

Third-party library caching is one of MethodCache's most powerful features, enabling:

- **Cost reduction** through fewer API calls
- **Performance improvement** via cached responses
- **Operational control** through runtime configuration
- **Emergency management** via highest-priority overrides

Remember: Runtime configuration has the **highest priority**, giving you ultimate control over all caching behavior, including third-party libraries!