# Runtime Configuration & Management Interface Overview

> **Status:** _Transitional – reflects the current dual-stack implementation._  
> Runtime overrides still flow through `CacheMethodSettings`/`MethodCacheConfigEntry`.  
> Improvements planned in `docs/developer/POLICY_PIPELINE_CONSOLIDATION_PLAN.md` will replace those types with policy-native APIs.

## 🔥 **What Makes This Special**

MethodCache now supports **runtime configuration with the highest priority**, enabling management interfaces that can **override ALL other configuration sources** including hardcoded values. This is a game-changer for operational control.

## **The Priority System**

| Priority | Source | Overrides | Use Case |
|----------|--------|-----------|----------|
| **40** 🔥 | **Runtime (Management)** | **Everything** | Emergency overrides, A/B testing |
| **30** | **Code (Programmatic)** | JSON/YAML/Attributes | Business logic |
| **20** | **JSON/YAML** | Attributes | Environment configs |
| **10** | **Attributes** | Nothing | Development defaults |

## **Real-World Scenarios**

### 🚨 **Emergency Response**
```bash
# Your payment processing cache is causing issues
# Operations team can instantly disable it:
curl -X POST /api/admin/cache/emergency-disable \
  -d '{"ServiceName": "IPaymentService", "MethodName": "ProcessPayment"}'

# ✅ Instantly overrides developer's [Cache] attribute and code configuration
```

### ⚡ **Live Performance Tuning**
```bash
# CPU usage is high due to expensive user profile queries
# Tune cache duration from 5 minutes to 1 hour:
curl -X POST /api/admin/cache/tune-performance \
  -d '{"ServiceName": "IUserService", "MethodName": "GetProfile", "Duration": "01:00:00"}'

# ✅ Immediately improves performance without code deployment
```

### 🔬 **A/B Testing**
```bash
# Test aggressive vs conservative caching strategies:
curl -X POST /api/admin/cache/ab-test \
  -d '{"ServiceName": "IProductService", "MethodName": "GetRecommendations", "Variant": "aggressive"}'

# ✅ Overrides developer settings for experimentation
```

## 🔍 Inspect Effective Policies

MethodCache exposes a `PolicyDiagnosticsService` that surfaces the resolver output so hosts can understand exactly which configuration layers contributed to a method.

```csharp
var diagnostics = host.Services.GetRequiredService<PolicyDiagnosticsService>();

foreach (var report in diagnostics.GetAllPolicies())
{
    var duration = report.Policy.Duration?.ToString() ?? "(default)";
    var sources = string.Join(", ", report.Contributions.Select(c => c.SourceId).Distinct());
    Console.WriteLine($"{report.MethodId}: Duration={duration}, Sources={sources}");
}
```

Each `PolicyDiagnosticsReport` includes the effective policy plus every `PolicyContribution`, grouped by source, making it easy to diff runtime overrides against baseline attribute/programmatic configuration.

## **How It Works**

### 1. **Developer Sets Defaults** (Priority 10-30)
```csharp
// Code defaults
[Cache("user-profile", Duration = "00:05:00")]
public async Task<UserProfile> GetUserProfileAsync(int userId) { ... }

// Programmatic overrides
config.ForService<IUserService>()
      .Method(x => x.GetUserProfileAsync(default))
      .Duration(TimeSpan.FromMinutes(30));
```

### 2. **Operations Team Controls** (Priority 40)
```json
// appsettings.json - Highest priority via IOptionsMonitor
{
  "MethodCache": {
    "Services": {
      "IUserService": {
        "Methods": {
          "GetUserProfileAsync": {
            "Duration": "02:00:00",    // ✅ This wins!
            "Enabled": true,
            "Tags": ["management-override"]
          }
        }
      }
    }
  }
}
```

### 3. **Management Interface** (Ultimate Control)
```csharp
[ApiController]
[Route("api/admin/cache")]
public sealed class CacheManagementController : ControllerBase
{
    private readonly IRuntimeCacheConfigurator _configurator;

    public CacheManagementController(IRuntimeCacheConfigurator configurator)
        => _configurator = configurator;

    [HttpPost("emergency-disable")]
    public async Task<IActionResult> EmergencyDisableCache([FromBody] DisableRequest request)
    {
        await _configurator.ApplyOverridesAsync(new[]
        {
            new MethodCacheConfigEntry
            {
                ServiceType = request.Service,
                MethodName = request.Method,
                Settings = new CacheMethodSettings
                {
                    Duration = TimeSpan.Zero,
                    Tags = new List<string> { "disabled" }
                }
            }
        });

        return Ok("Cache disabled - effective immediately");
    }
}
```

## **Integration Examples**

### **Azure App Configuration**
```csharp
builder.Configuration.AddAzureAppConfiguration(options =>
{
    options.Connect(connectionString)
           .Select(KeyFilter.Any, "MethodCache")
           .ConfigureRefresh(refresh => refresh.Register("MethodCache:RefreshKey"));
});

// Runtime config automatically gets Azure App Config changes
builder.Services.AddMethodCacheWithSources(cache => {
    cache.AddRuntimeConfiguration(); // Highest priority
    cache.AddProgrammaticConfiguration(/* ... */);
    cache.AddJsonConfiguration(builder.Configuration);
});
```

### **Configuration Dashboard**
```csharp
[HttpGet("status")]
public IActionResult GetCacheStatus()
{
    var registry = _serviceProvider.GetRequiredService<IPolicyRegistry>();
    var policies = registry.GetAllPolicies();
    
    return Ok(policies.Select(result => new {
        Method = result.MethodId,
        Duration = result.Policy.Duration?.ToString(),
        Tags = result.Policy.Tags,
        Source = string.Join(", ", result.Contributions.Select(c => c.SourceId).Distinct())
    }));
}
```

## **Why This Matters**

### **For SREs & Operations**
- **Instant incident response** - disable problematic caches immediately
- **Performance tuning** - adjust cache durations based on real metrics
- **Capacity management** - reduce cache pressure during high load

### **For Product Teams**
- **A/B testing** - test different caching strategies without code changes
- **Feature flags** - enable/disable caching for specific features
- **Gradual rollouts** - progressively increase cache durations

### **For Development Teams**
- **Safe defaults** - set reasonable defaults in code
- **Environment separation** - different settings for dev/staging/prod
- **Override assurance** - operations can always override if needed

## **Key Benefits**

✅ **Operational Safety** - Operations can always override developer decisions  
✅ **Zero Downtime** - Change cache behavior without deployments  
✅ **Incident Response** - Instantly disable problematic caches  
✅ **Performance Optimization** - Tune based on real usage patterns  
✅ **A/B Testing** - Experiment with different caching strategies  
✅ **Compliance** - Override caching for sensitive data on demand  

## **Getting Started**

```csharp
// 1. Setup multi-source configuration
builder.Services.AddMethodCacheWithSources(cache => {
    cache.AddAttributeSource();                    // Development defaults
    cache.AddJsonConfiguration(builder.Configuration); // Environment configs  
    cache.AddRuntimeConfiguration();               // 🔥 Management overrides
});

// 2. Build management endpoints
// See CONFIGURATION_GUIDE.md for complete examples

// 3. Configure external config store (Azure App Config, etc.)
// Changes automatically flow through IOptionsMonitor
```

**The result: Ultimate operational control over caching behavior!** 🎯

---

📖 **[Full Documentation →](CONFIGURATION_GUIDE.md)**  
🏗️ **[Implementation Details →](CONFIGURATION_DEMO.md)**
