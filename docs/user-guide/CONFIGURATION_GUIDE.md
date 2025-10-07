# MethodCache Configuration Guide

This guide covers all the ways you can configure MethodCache, from simple attribute-based configuration to complex multi-source configurations with runtime updates.

## 🔥 Key Feature: Runtime Management Interface

**MethodCache supports runtime configuration overrides with the highest priority**, enabling powerful management interfaces that can override ALL other configuration sources (including code). This allows operations teams to:

- **🚨 Emergency disable** problematic caches instantly
- **⚡ Live tune** performance during incidents  
- **🔬 A/B test** different caching strategies
- **🛡️ Override** developer settings for compliance
- **📊 Dynamically optimize** based on real usage

[Jump to Management Interface Examples →](#management-interface--runtime-overrides)

## Table of Contents

1. [Configuration Overview](#configuration-overview)
2. [Attribute-Based Configuration](#attribute-based-configuration)
3. [Method Chaining API Configuration](#method-chaining-api-configuration) 🆕
4. [Programmatic Configuration](#programmatic-configuration)
5. [JSON Configuration](#json-configuration)
6. [YAML Configuration](#yaml-configuration)
7. [Runtime Configuration](#runtime-configuration)
8. [Management Interface & Runtime Overrides](#management-interface--runtime-overrides)
9. [Multi-Source Configuration](#multi-source-configuration)
10. [Configuration Priorities](#configuration-priorities)
11. [Configuration Validation](#configuration-validation)
12. [Examples](#examples)

## Configuration Overview

MethodCache supports multiple configuration sources that can be combined:

| Source | Priority | Runtime Updates | Use Case |
|--------|----------|-----------------|----------|
| Attributes | 10 (lowest) | ❌ | Development, defaults |
| JSON | 20 | ✅ | Operations, environments |
| YAML | 20 | ✅ | Operations, complex configs |
| Programmatic | 30 | ❌ | Code-based overrides |
| Runtime (IOptions) | 40 (highest) | ✅ | Management interface, emergency overrides |

## Attribute-Based Configuration

The simplest way to configure caching using attributes on interface methods:

```csharp
public interface IUserService
{
    [Cache("user-profile", Duration = "01:00:00", Tags = new[] { "user", "profile" })]
    Task<UserProfile> GetUserProfileAsync(int userId);
    
    [Cache("user-settings", Duration = "00:30:00")]
    [ETag(Strategy = ETagGenerationStrategy.ContentHash, UseWeakETag = false)]
    Task<UserSettings> GetUserSettingsAsync(int userId);
    
    [CacheInvalidate(Tags = new[] { "user" })]
    Task UpdateUserAsync(int userId, UserUpdateModel update);
}

// Register with attribute scanning
builder.Services.AddMethodCache(assemblies: Assembly.GetExecutingAssembly());
```

## Method Chaining API Configuration

**NEW!** The Method Chaining API provides an intuitive, chainable interface for configuring cache operations. Perfect for caching third-party libraries, legacy code, or when you prefer explicit control over attribute-based configuration.

### Basic Usage

```csharp
// Inject ICacheManager and use fluent chaining
public class UserService
{
    private readonly ICacheManager _cache;
    private readonly IUserRepository _repository;

    public async Task<User> GetUserAsync(int userId)
    {
        return await _cache.Cache(() => _repository.GetUserAsync(userId))
            .WithDuration(TimeSpan.FromHours(1))
            .WithTags("user", $"user:{userId}")
            .ExecuteAsync();
    }
}
```

### Advanced Configuration

```csharp
public async Task<List<Order>> GetOrdersWithAdvancedConfig(int customerId, OrderStatus status)
{
    return await _cache.Cache(() => _orderService.GetOrdersAsync(customerId, status))
        .WithDuration(TimeSpan.FromMinutes(30))
        .WithSlidingExpiration(TimeSpan.FromMinutes(10))
        .WithRefreshAhead(TimeSpan.FromMinutes(5))
        .WithTags("orders", $"customer:{customerId}", $"status:{status}")
        .WithStampedeProtection(StampedeProtectionMode.Probabilistic, beta: 1.5)
        .WithDistributedLock(TimeSpan.FromSeconds(30), maxConcurrency: 2)
        .WithKeyGenerator<JsonKeyGenerator>()
        .WithVersion(2)
        .OnHit(ctx => _logger.LogInformation($"Cache hit: {ctx.Key}"))
        .OnMiss(ctx => _logger.LogInformation($"Cache miss: {ctx.Key}"))
        .When(ctx => customerId > 0) // Conditional caching
        .ExecuteAsync();
}
```

### Key Generator Selection

```csharp
// Choose optimal key generator based on scenario
public async Task<DataResult> GetDataWithOptimalKeyGenerator(QueryRequest request)
{
    var builder = _cache.Cache(() => _dataService.ProcessQueryAsync(request))
        .WithDuration(TimeSpan.FromMinutes(30));

    // Dynamic key generator selection
    if (request.Parameters.Count > 10)
        builder = builder.WithKeyGenerator<MessagePackKeyGenerator>(); // Complex objects
    else if (request.IsDebugMode)
        builder = builder.WithKeyGenerator<JsonKeyGenerator>(); // Human-readable
    else
        builder = builder.WithKeyGenerator<FastHashKeyGenerator>(); // Performance

    return await builder.ExecuteAsync();
}
```

### Conditional Caching

```csharp
// Cache based on business logic
public async Task<AnalyticsReport> GetReportConditionally(ReportCriteria criteria, bool isPremiumUser)
{
    return await _cache.Cache(() => _reportService.GenerateReportAsync(criteria))
        .WithDuration(isPremiumUser ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(30))
        .WithTags("analytics", isPremiumUser ? "premium" : "standard")
        .When(ctx => criteria.IsExpensive) // Only cache expensive reports
        .WithKeyGenerator<MessagePackKeyGenerator>()
        .ExecuteAsync();
}
```

### Third-Party Library Caching

```csharp
// Cache external API calls without modifying the library
public class WeatherService
{
    private readonly IWeatherApiClient _weatherApi;
    private readonly ICacheManager _cache;

    public async Task<WeatherData> GetWeatherAsync(string location)
    {
        return await _cache.Cache(() => _weatherApi.GetCurrentWeatherAsync(location))
            .WithDuration(TimeSpan.FromMinutes(15))
            .WithTags("weather", $"location:{location}")
            .WithKeyGenerator<JsonKeyGenerator>() // Debug-friendly keys
            .OnHit(ctx => _metrics.RecordWeatherCacheHit())
            .OnMiss(ctx => _metrics.RecordWeatherApiCall())
            .ExecuteAsync();
    }
}
```

### Alternative API Syntax

```csharp
// Both APIs are equivalent - choose your preference
// Option 1: Cache()
var result1 = await cache.Cache(() => service.GetDataAsync(id))
    .WithDuration(TimeSpan.FromHours(1))
    .ExecuteAsync();

// Option 2: Build()
var result2 = await cache.Build(() => service.GetDataAsync(id))
    .WithDuration(TimeSpan.FromHours(1))
    .ExecuteAsync();
```

### Benefits of Method Chaining API

- **Intuitive**: Natural reading flow with discoverable methods
- **Type-safe**: Generic key generator selection with compile-time validation
- **Flexible**: Conditional configuration based on runtime values
- **Performance**: No overhead - compiles to efficient code
- **Testable**: Easy to mock and unit test
- **Backward Compatible**: Works alongside existing attribute and configuration-based approaches

## Programmatic Configuration

Configure caching entirely in code:

```csharp
builder.Services.AddMethodCache(config => {
    // Global defaults
    config.DefaultDuration(TimeSpan.FromMinutes(15));
    
    // Service-specific configuration
    config.ForService<IUserService>()
          .Method(x => x.GetUserProfileAsync(default))
          .Duration(TimeSpan.FromHours(1))
          .Tags("user", "profile")
          .Version(5)
          .KeyGenerator<StableUserKeyGenerator>()
          .When(ctx => ctx.GetArg<int>(0) != 0)
          .OnHit(ctx => Console.WriteLine("Cache hit!"));
          
    // Group configuration
    config.ForGroup("user-data")
          .Duration(TimeSpan.FromMinutes(30))
          .Tags("user");
});
```

```csharp
public sealed class StableUserKeyGenerator : ICacheKeyGenerator
{
    public string GenerateKey(string methodName, object[] args, CacheMethodSettings settings)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("Key generation requires at least one argument for this generator.", nameof(args));
        }
        return $"{methodName}:{args[0]}";
    }
}
```

## JSON Configuration

Configure via `appsettings.json` or other JSON configuration sources:

### appsettings.json
```json
{
  "MethodCache": {
    "Defaults": {
      "Duration": "00:15:00",
      "Tags": ["default"],
      "ETag": {
        "Strategy": "ContentHash",
        "IncludeParametersInETag": true
      }
    },
    "Services": {
      "MyApp.Services.IUserService.GetUserProfile": {
        "Duration": "01:00:00",
        "Tags": ["user", "profile"],
        "Version": 1,
        "ETag": {
          "Strategy": "ContentHash",
          "UseWeakETag": false,
          "CacheDuration": "02:00:00"
        }
      },
      "MyApp.Services.IProductService.GetProduct": {
        "Duration": "00:30:00",
        "Tags": ["product", "catalog"]
      }
    }
  }
}
```

### Usage
```csharp
builder.Services.AddMethodCacheWithSources(cache => {
    cache.AddJsonConfiguration(builder.Configuration);
    cache.AddAttributeSource(); // Still include attributes as fallback
});
```

## YAML Configuration

Use YAML for more readable configuration files:

### cache-config.yaml
```yaml
defaults:
  duration: "00:15:00"
  tags:
    - "default"
  etag:
    strategy: "ContentHash"
    includeParametersInETag: true

services:
  "MyApp.Services.IUserService.GetUserProfile":
    duration: "01:00:00"
    tags:
      - "user"
      - "profile"
    version: 1
    etag:
      strategy: "ContentHash"
      useWeakETag: false
      cacheDuration: "02:00:00"
  
  "MyApp.Services.IProductService.GetProduct":
    duration: "00:30:00"
    tags:
      - "product"
      - "catalog"
```

### Usage
```csharp
builder.Services.AddMethodCacheWithSources(cache => {
    cache.AddYamlConfiguration("cache-config.yaml");
    cache.AddAttributeSource();
});
```

## Runtime Configuration

Configure caching that can be updated at runtime using IOptionsMonitor:

### appsettings.json
```json
{
  "MethodCache": {
    "DefaultDuration": "00:15:00",
    "GlobalTags": ["runtime"],
    "EnableDebugLogging": true,
    "EnableMetrics": true,
    "Services": {
      "MyApp.Services.IUserService": {
        "DefaultDuration": "01:00:00",
        "DefaultTags": ["user"],
        "Methods": {
          "GetUserProfile": {
            "Duration": "02:00:00",
            "Tags": ["profile"],
            "Enabled": true,
            "ETag": {
              "Strategy": "ContentHash",
              "IncludeParametersInETag": true
            }
          },
          "GetUserSettings": {
            "Enabled": false
          }
        }
      }
    }
  }
}
```

### Usage
```csharp
builder.Services.AddMethodCacheWithSources(cache => {
    cache.AddRuntimeConfiguration(options => {
        // Additional programmatic configuration
        options.EnableMetrics = true;
    });
    cache.AddAttributeSource();
});

// Configuration will automatically reload when appsettings.json changes
```

## Management Interface & Runtime Overrides
### Inspect Runtime State

You can inspect the merged runtime configuration through the `PolicyDiagnosticsService` which is automatically registered when you call `AddMethodCacheWithSources`.

```csharp
var diagnostics = provider.GetRequiredService<PolicyDiagnosticsService>();

foreach (var policy in diagnostics.GetAllPolicies())
{
    Console.WriteLine($"{policy.MethodId}: Duration={policy.Policy.Duration ?? TimeSpan.Zero}, Sources={string.Join(", ", policy.Contributions.Select(c => c.SourceId))}");
}
```

Each report exposes the full set of `PolicyContribution`s so you can see exactly which layer (attributes, JSON/YAML, programmatic, runtime) produced the effective configuration.


**🔥 CRITICAL FEATURE:** The runtime layer has the **highest priority** and overrides every other configuration surface. The DI container exposes `IRuntimeCacheConfigurator`, giving you a single entry point for management UIs, incident tooling, or scripting.

### Runtime management API surface

| Method | What it does |
|--------|---------------|
| `ApplyFluentAsync(Action<IFluentMethodCacheConfiguration>)` | Reuses the fluent builders you already use at startup. Great for strongly typed overrides. |
| `ApplyOverridesAsync(IEnumerable<MethodCacheConfigEntry>)` | Accepts raw method keys/settings – perfect for UI forms or persisted runtime rules. |
| `GetOverridesAsync()` | Returns every live override (cloned so callers can edit safely). |
| `RemoveOverrideAsync(serviceType, methodName)` | Removes a single method override; returns `false` if nothing was there. |
| `ClearOverridesAsync()` | Drops the runtime layer entirely. |
| `GetEffectiveConfigurationAsync()` | Provides the fully merged view after attributes, config files, and runtime overrides. |

### Example: Cache management endpoints

```csharp
[ApiController]
[Route("api/admin/cache")]
public sealed class CacheManagementController : ControllerBase
{
    private readonly IRuntimeCacheConfigurator _configurator;

    public CacheManagementController(IRuntimeCacheConfigurator configurator)
        => _configurator = configurator;

    // Strongly-typed override (great for known hotspots)
    [HttpPost("orders/boost")]
    public async Task<IActionResult> BoostOrdersAsync([FromBody] TimeSpan duration)
    {
        await _configurator.ApplyFluentAsync(fluent =>
        {
            fluent.ForService<IOrdersService>()
                  .Method(s => s.GetAsync(default))
                  .Configure(o => o
                      .WithDuration(duration)
                      .WithTags("runtime", "orders"));
        });

        return Ok();
    }

    // Dynamic override driven by UI form input
    [HttpPost("overrides")]
    public async Task<IActionResult> UpsertOverride([FromBody] CacheOverrideRequest request)
    {
        var entry = new MethodCacheConfigEntry
        {
            ServiceType = request.ServiceType,
            MethodName = request.MethodName,
            Settings = new CacheMethodSettings
            {
                Duration = request.Duration,
                Tags = request.Tags?.ToList() ?? new List<string>(),
                IsIdempotent = request.RequireIdempotency
            }
        };

        await _configurator.ApplyOverridesAsync(new[] { entry });
        return Ok();
    }

    [HttpDelete("overrides")]
    public async Task<IActionResult> RemoveOverride([FromQuery] string service, [FromQuery] string method)
    {
        var removed = await _configurator.RemoveOverrideAsync(service, method);
        return removed ? NoContent() : NotFound();
    }

    [HttpDelete("overrides/all")]
    public async Task<IActionResult> ClearOverrides()
    {
        await _configurator.ClearOverridesAsync();
        return NoContent();
    }

    [HttpGet("overrides")]
    public async Task<IReadOnlyList<MethodCacheConfigEntry>> GetOverrides()
        => await _configurator.GetOverridesAsync();

    [HttpGet("effective")]
    public async Task<IActionResult> GetEffectiveConfiguration()
    {
        var entries = await _configurator.GetEffectiveConfigurationAsync();
        return Ok(entries.Select(e => new
        {
            e.ServiceType,
            e.MethodName,
            Duration = e.Settings.Duration,
            Tags = e.Settings.Tags,
            e.Priority
        }));
    }
}

public sealed record CacheOverrideRequest(
    string ServiceType,
    string MethodName,
    TimeSpan? Duration,
    string[]? Tags,
    bool RequireIdempotency);
```

With these endpoints you can build dashboards that:

- **🚨 Kill a cache** during incidents (`ClearOverridesAsync` or `RemoveOverrideAsync`).
- **⚡ Tune durations** on the fly (`ApplyOverridesAsync`).
- **🔬 Run experiments** by applying different overrides and comparing metrics.
- **📊 Visualize the truth** with `GetEffectiveConfigurationAsync` – what the system is actually using right now.

### Integration with External Configuration Stores

Runtime configuration works seamlessly with external configuration providers:

#### Azure App Configuration
```csharp
builder.Configuration.AddAzureAppConfiguration(options =>
{
    options.Connect(connectionString)
           .Select(KeyFilter.Any, "MethodCache")
           .ConfigureRefresh(refreshOptions =>
           {
               refreshOptions.Register("MethodCache:RefreshKey", refreshAll: true)
                           .SetCacheExpiration(TimeSpan.FromSeconds(30));
           });
});

// Runtime configuration automatically picks up Azure App Config changes
builder.Services.AddMethodCacheWithSources(cache => {
    cache.AddRuntimeConfiguration(); // Gets Azure App Config changes automatically
});
```

#### Redis Configuration Store
```csharp
public class RedisConfigurationService
{
    private readonly IDatabase _redis;
    private readonly IConfiguration _configuration;
    
    public async Task UpdateCacheSettingAsync(string methodKey, object settings)
    {
        var configKey = $"MethodCache:Services:{methodKey}";
        var json = JsonSerializer.Serialize(settings);
        
        // Update Redis
        await _redis.StringSetAsync(configKey, json);
        
        // Trigger configuration reload
        _configuration.GetReloadToken().ActiveChangeCallback?.Invoke(null);
    }
}
```

### Monitoring Configuration Changes

```csharp
public class ConfigurationChangeMonitor
{
    private readonly IOptionsMonitor<MethodCacheOptions> _optionsMonitor;
    private readonly ILogger<ConfigurationChangeMonitor> _logger;
    
    public ConfigurationChangeMonitor(
        IOptionsMonitor<MethodCacheOptions> optionsMonitor,
        ILogger<ConfigurationChangeMonitor> logger)
    {
        _optionsMonitor = optionsMonitor;
        _logger = logger;
        
        // Monitor for changes
        _optionsMonitor.OnChange((options, name) =>
        {
            _logger.LogInformation("Cache configuration changed for {ConfigName} at {Timestamp}", 
                name, DateTime.UtcNow);
            
            // Log specific changes
            foreach (var service in options.Services)
            {
                foreach (var method in service.Value.Methods)
                {
                    _logger.LogInformation("Method {Service}.{Method} configured: Duration={Duration}, Enabled={Enabled}",
                        service.Key, method.Key, method.Value.Duration, method.Value.Enabled);
                }
            }
        });
    }
}
```

### Configuration Override Examples

To demonstrate the power of runtime overrides:

```csharp
// 1. Code says cache for 1 hour
[Cache("user-profile", Duration = "01:00:00")]
public async Task<UserProfile> GetUserProfileAsync(int userId) { ... }

// 2. JSON config says cache for 30 minutes  
// appsettings.json: "Duration": "00:30:00"

// 3. Programmatic config says cache for 2 hours
config.ForService<IUserService>()
      .Method(x => x.GetUserProfileAsync(default))
      .Duration(TimeSpan.FromHours(2));

// 4. 🔥 RUNTIME CONFIG WINS - Management interface sets 5 minutes
// POST /api/admin/cache/emergency-tune
// { "Duration": "00:05:00" }

// ✅ Result: 5 minutes (runtime override wins!)
```

This architecture ensures that **operations teams have ultimate control** over caching behavior, which is essential for production systems.

## Multi-Source Configuration

Combine multiple configuration sources with proper priority handling:

```csharp
builder.Services.AddMethodCacheWithSources(cache => {
    // Lowest priority: Attributes (development defaults)
    cache.AddAttributeSource(Assembly.GetExecutingAssembly());
    
    // Medium priority: JSON configuration (ops team)
    cache.AddJsonConfiguration(builder.Configuration);
    
    // Medium priority: YAML configuration (complex scenarios)
    cache.AddYamlConfiguration("cache-config.yaml");
    
    // High priority: Programmatic overrides (code-based)
    cache.AddProgrammaticConfiguration(prog => {
        prog.AddMethod("MyApp.Services.ICriticalService", "ProcessPayment", settings => {
            settings.Duration = TimeSpan.FromMinutes(5); // Code-based setting
            settings.Tags.Add("payment");
        });
    });
    
    // Highest priority: Runtime configuration (management interface - can override everything)
    cache.AddRuntimeConfiguration();
});
```

## Configuration Priorities

When multiple sources configure the same method, priority determines the winner:

1. **Runtime Configuration** (Priority 40) - **Always wins** - Management interface control
2. **Programmatic Configuration** (Priority 30) - Code-based overrides
3. **JSON/YAML Configuration** (Priority 20) - Operations team control  
4. **Attribute Configuration** (Priority 10) - Development defaults

### Example Priority Resolution

```csharp
// Attribute
[Cache(Duration = "00:05:00")] // ❌ Will be overridden

// JSON (appsettings.json)
"Duration": "00:15:00"         // ❌ Will be overridden

// Programmatic
.Duration(TimeSpan.FromHours(1)) // ❌ Will be overridden

// Runtime (highest priority - management interface)
"Duration": "00:30:00"         // ✅ This wins!
```

## Configuration Validation

MethodCache performs comprehensive validation to ensure configuration integrity:

### Built-in Validation Rules

| Parameter | Validation Rule | Error Message |
|-----------|----------------|---------------|
| Duration | Must be positive (> 0) | "Duration must be positive" |
| SlidingExpiration | Must be positive (> 0) | "Sliding expiration must be positive" |
| RefreshAhead | Must be positive (> 0) | "Refresh window must be positive" |
| StampedeProtection.Beta | Must be positive (> 0) | "Beta must be positive" |
| StampedeProtection.RefreshAheadWindow | Must be positive (> 0) | "Refresh ahead window must be positive" |
| DistributedLock.Timeout | Must be positive (> 0) | "Timeout must be positive" |
| DistributedLock.MaxConcurrency | Must be positive (> 0) | "Max concurrency must be positive" |
| Version | Must be non-negative (>= 0) | "Version must be non-negative" |
| SegmentSize | Must be positive (> 0) | "Segment size must be positive" |
| MaxMemorySize | Must be positive (> 0) | "Max memory size must be positive" |

### Validation Examples

```csharp
// ✅ Valid configuration
config.ForService<IUserService>()
    .Method(x => x.GetData(default))
    .Configure(options =>
    {
        options.WithDuration(TimeSpan.FromMinutes(30));     // ✅ Positive
        options.RefreshAhead(TimeSpan.FromSeconds(10));     // ✅ Positive
        options.WithVersion(1);                             // ✅ Non-negative
    });

// ❌ Invalid configuration - throws ArgumentOutOfRangeException
config.ForService<IUserService>()
    .Method(x => x.GetData(default))
    .Configure(options =>
    {
        options.WithDuration(TimeSpan.Zero);                // ❌ Not positive
        options.RefreshAhead(TimeSpan.FromSeconds(-1));     // ❌ Negative
        options.WithVersion(-1);                           // ❌ Negative
    });
```

### Custom Validation

You can add custom validation logic:

```csharp
builder.Services.AddMethodCache(config =>
{
    config.OnConfiguring(settings =>
    {
        // Custom validation
        if (settings.Duration > TimeSpan.FromDays(7))
        {
            throw new InvalidOperationException("Cache duration cannot exceed 7 days");
        }

        if (settings.Tags.Contains("temp") && settings.Duration > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException("Temporary caches cannot exceed 1 hour");
        }
    });
});
```

### Idempotency Validation

Methods marked with `RequireIdempotent` enforce idempotency at runtime:

```csharp
[Cache(RequireIdempotent = true)]
Task<Data> GetDataAsync(int id); // Must be marked as idempotent in config

// Configuration
config.ForService<IService>()
    .Method(x => x.GetDataAsync(default))
    .RequireIdempotent(true); // ✅ Matches requirement

// Runtime error if not idempotent:
// InvalidOperationException: Method GetDataAsync is not marked as idempotent, but caching requires it.
```

## Examples

### Simple Attribute-Only Setup
```csharp
// Program.cs
builder.Services.AddMethodCache(assemblies: Assembly.GetExecutingAssembly());

// Service
public interface IProductService
{
    [Cache("products", Duration = "01:00:00", Tags = new[] { "catalog" })]
    Task<Product[]> GetProductsAsync();
}
```

### Production Multi-Source Setup
```csharp
// Program.cs
builder.Services.AddMethodCacheWithSources(cache => {
    // Development defaults from attributes
    cache.AddAttributeSource();
    
    // Operations configuration from JSON
    cache.AddJsonConfiguration(builder.Configuration);
    
    // Runtime configuration for dynamic updates
    cache.AddRuntimeConfiguration();
    
    // Critical overrides in code
    cache.AddProgrammaticConfiguration(prog => {
        // Ensure payment processing is never cached too long
        prog.AddMethod("IPaymentService", "ProcessPayment", s => {
            s.Duration = TimeSpan.FromMinutes(1);
            s.Tags.Add("critical");
        });
    });
});

// Add ETag support
builder.Services.AddETagSupport();

// Add hybrid caching
builder.Services.AddHybridCache();
```

### Environment-Specific Configuration

#### appsettings.Development.json
```json
{
  "MethodCache": {
    "DefaultDuration": "00:01:00",
    "EnableDebugLogging": true,
    "Services": {
      "MyApp.Services.IUserService.GetUserProfile": {
        "Duration": "00:05:00"
      }
    }
  }
}
```

#### appsettings.Production.json
```json
{
  "MethodCache": {
    "DefaultDuration": "01:00:00",
    "EnableDebugLogging": false,
    "EnableMetrics": true,
    "Services": {
      "MyApp.Services.IUserService.GetUserProfile": {
        "Duration": "24:00:00"
      }
    }
  }
}
```

### Dynamic Runtime Updates Example

```csharp
// Service for updating configuration at runtime
public class CacheConfigurationService
{
    private readonly IOptionsSnapshot<MethodCacheOptions> _options;
    private readonly IConfiguration _configuration;
    
    public CacheConfigurationService(
        IOptionsSnapshot<MethodCacheOptions> options,
        IConfiguration configuration)
    {
        _options = options;
        _configuration = configuration;
    }
    
    public async Task UpdateCacheDurationAsync(string service, string method, TimeSpan duration)
    {
        // Update configuration source (Azure App Configuration, etc.)
        var key = $"MethodCache:Services:{service}:Methods:{method}:Duration";
        await _configuration[key] = duration.ToString();
        
        // Configuration will automatically reload via IOptionsMonitor
    }
}
```

## Configuration Validation

Enable validation to catch configuration errors early:

```csharp
builder.Services.AddOptions<MethodCacheOptions>()
    .Bind(builder.Configuration.GetSection("MethodCache"))
    .ValidateDataAnnotations()
    .Validate(options => {
        // Custom validation logic
        if (options.DefaultDuration > TimeSpan.FromDays(1))
        {
            return false; // Duration too long
        }
        return true;
    });
```

## Management Interface Benefits

With runtime configuration having the highest priority, you can build powerful management interfaces:

### Emergency Cache Control
```csharp
// Management API endpoint
[HttpPost("/admin/cache/emergency-disable")]
public async Task EmergencyDisableCache(string service, string method)
{
    // This will override ALL other configuration sources
    await _configurator.ApplyOverridesAsync(new[]
    {
        new MethodCacheConfigEntry
        {
            ServiceType = service,
            MethodName = method,
            Settings = new CacheMethodSettings
            {
                Duration = TimeSpan.Zero,
                Tags = new List<string> { "emergency-disable" }
            }
        }
    });
}
```

### Live Performance Tuning
```csharp
[HttpPost("/admin/cache/tune-performance")]
public async Task TunePerformance(string service, string method, TimeSpan duration)
{
    // Override programmatic settings for live tuning
    await _configurator.ApplyOverridesAsync(new[]
    {
        new MethodCacheConfigEntry
        {
            ServiceType = service,
            MethodName = method,
            Settings = new CacheMethodSettings
            {
                Duration = duration,
                Tags = new List<string> { "performance-tuned" }
            }
        }
    });
}
```

### A/B Testing Framework
```csharp
[HttpPost("/admin/cache/ab-test")]
public async Task SetupABTest(string service, string method, string variant)
{
    var settings = variant switch
    {
        "aggressive" => (Duration: TimeSpan.FromHours(1), Tags: new[] { "ab-test", "aggressive" }),
        "conservative" => (Duration: TimeSpan.FromMinutes(5), Tags: new[] { "ab-test", "conservative" }),
        _ => throw new ArgumentException("Invalid variant")
    };

    await _configurator.ApplyOverridesAsync(new[]
    {
        new MethodCacheConfigEntry
        {
            ServiceType = service,
            MethodName = method,
            Settings = new CacheMethodSettings
            {
                Duration = settings.Duration,
                Tags = settings.Tags.ToList()
            }
        }
    });
}
```

## Best Practices

1. **Use Attributes for Defaults** - Good for development and basic scenarios
2. **Use JSON/YAML for Operations** - Let ops teams adjust without code changes
3. **Use Programmatic for Code-based Logic** - Implement consistent application logic
4. **Use Runtime Configuration for Management** - Override everything from management interfaces
5. **Layer Configuration Sources** - Start simple, add complexity as needed
6. **Monitor Configuration Changes** - Log when configurations change at runtime
7. **Validate Configuration** - Use built-in validation to catch errors early

This layered approach gives you maximum flexibility while maintaining simplicity where possible!

## Quick Reference: Configuration Priority

| Priority | Source | Can Override | Runtime Updates | Best For |
|----------|--------|--------------|-----------------|----------|
| **40** 🔥 | **Runtime (IOptionsMonitor)** | **Everything** | ✅ Yes | Management interfaces, emergency overrides |
| **30** | **Programmatic (Code)** | JSON/YAML/Attributes | ❌ No | Application logic, business rules |
| **20** | **JSON/YAML** | Attributes only | ✅ Yes | Environment configs, operations |
| **10** | **Attributes** | Nothing | ❌ No | Development defaults |

### 🚨 Emergency Override Example

```bash
# Emergency: Disable all user profile caching via management API
curl -X POST /api/admin/cache/emergency-disable \
  -d '{"ServiceName": "IUserService", "MethodName": "GetUserProfile"}'

# ✅ Instantly overrides ALL other configuration sources!
```

**Remember: Runtime configuration = Ultimate operational control** 🎯
