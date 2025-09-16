# MethodCache Configuration Guide

This guide covers all the ways you can configure MethodCache, from simple attribute-based configuration to complex multi-source configurations with runtime updates.

## Table of Contents

1. [Configuration Overview](#configuration-overview)
2. [Attribute-Based Configuration](#attribute-based-configuration)
3. [Programmatic Configuration](#programmatic-configuration)
4. [JSON Configuration](#json-configuration)
5. [YAML Configuration](#yaml-configuration)
6. [Runtime Configuration](#runtime-configuration)
7. [Multi-Source Configuration](#multi-source-configuration)
8. [Configuration Priorities](#configuration-priorities)
9. [Examples](#examples)

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
          .OnHit(ctx => Console.WriteLine("Cache hit!"));
          
    // Group configuration
    config.ForGroup("user-data")
          .Duration(TimeSpan.FromMinutes(30))
          .Tags("user");
});
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
    await _configurationService.UpdateCacheSettingAsync($"{service}.{method}", new {
        Enabled = false,
        Duration = "00:00:01" // Minimal cache
    });
    
    // Configuration automatically reloads via IOptionsMonitor
}
```

### Live Performance Tuning
```csharp
[HttpPost("/admin/cache/tune-performance")]
public async Task TunePerformance(string service, string method, TimeSpan duration)
{
    // Override programmatic settings for live tuning
    await _configurationService.UpdateCacheSettingAsync($"{service}.{method}", new {
        Duration = duration.ToString(),
        Tags = new[] { "performance-tuned" }
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
        "aggressive" => new { Duration = "01:00:00", Tags = new[] { "ab-test", "aggressive" } },
        "conservative" => new { Duration = "00:05:00", Tags = new[] { "ab-test", "conservative" } },
        _ => throw new ArgumentException("Invalid variant")
    };
    
    await _configurationService.UpdateCacheSettingAsync($"{service}.{method}", settings);
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