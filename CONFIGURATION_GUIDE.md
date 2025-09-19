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
3. [Programmatic Configuration](#programmatic-configuration)
4. [JSON Configuration](#json-configuration)
5. [YAML Configuration](#yaml-configuration)
6. [Runtime Configuration](#runtime-configuration)
7. [Management Interface & Runtime Overrides](#management-interface--runtime-overrides)
8. [Multi-Source Configuration](#multi-source-configuration)
9. [Configuration Priorities](#configuration-priorities)
10. [Examples](#examples)

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
        => $"{methodName}:{args[0]}";
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
