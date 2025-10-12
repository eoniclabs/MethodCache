# Policy Pipeline Migration Guide

This guide explains the new policy pipeline architecture introduced in MethodCache 1.1.x and how to migrate from earlier versions.

## What Changed?

MethodCache now uses a **unified policy pipeline** for all configuration sources. This is an **internal architectural change** that improves consistency, enables better diagnostics, and provides a foundation for advanced features.

### Before (v1.0.x)
```
Attributes → MethodCacheConfiguration (direct mutation)
Fluent API → MethodCacheConfiguration (direct mutation)
JSON/YAML → MethodCacheConfiguration (via sources)
Runtime → MethodCacheConfiguration (direct mutation)
```

### After (v1.1.x+)
```
All Sources → Policy Pipeline → PolicyResolver → IPolicyRegistry → Decorators
```

## Impact on Your Code

**Good news: Your existing code continues to work!**

All public APIs remain the same:
- ✅ `[Cache]` and `[CacheInvalidate]` attributes work unchanged
- ✅ `AddMethodCache()`, `AddMethodCacheFluent()` work unchanged
- ✅ JSON/YAML configuration works unchanged
- ✅ Runtime overrides via `IRuntimeCacheConfigurator` work unchanged

## What You Get

### 1. Correct Priority Handling

Configuration sources now have well-defined priorities:

| Source | Priority |
|--------|----------|
| Attributes | 10 |
| Fluent/Programmatic | 40 |
| JSON/YAML | 50 |
| Runtime Overrides | 100 |

**Higher priority always wins** when multiple sources configure the same method.

### 2. Policy Diagnostics

New `PolicyDiagnosticsService` lets you inspect the effective configuration:

```csharp
var diagnostics = serviceProvider.GetRequiredService<PolicyDiagnosticsService>();

// Get all policies
foreach (var report in diagnostics.GetAllPolicies())
{
    Console.WriteLine($"Method: {report.MethodId}");
    Console.WriteLine($"  Duration: {report.Policy.Duration}");
    Console.WriteLine($"  Sources: {string.Join(", ", report.Contributions.Select(c => c.SourceId))}");
}

// Get policy for specific method
var userPolicy = diagnostics.GetPolicy("MyApp.IUserService.GetUserAsync");
if (userPolicy != null)
{
    Console.WriteLine($"Cache duration: {userPolicy.Policy.Duration}");

    // See which sources contributed
    foreach (var contribution in userPolicy.Contributions)
    {
        Console.WriteLine($"  - {contribution.SourceId} set {contribution.Fields}");
    }
}
```

### 3. Runtime Overrides Now Work Correctly

Runtime overrides now have the highest priority (100), ensuring they actually override everything else:

```csharp
// This NOW correctly overrides all other configuration
var configurator = serviceProvider.GetRequiredService<IRuntimeCacheConfigurator>();
configurator.OverrideMethod("MyApp.IUserService.GetUserAsync", settings =>
{
    settings.Duration = TimeSpan.FromMinutes(5); // ✅ Takes effect immediately
});
```

## Migration Checklist

### ✅ No Action Required

- ✓ Existing `[Cache]` attributes continue to work
- ✓ `AddMethodCache()` registration continues to work
- ✓ JSON/YAML configuration files continue to work
- ✓ Fluent API `config.AddMethod()` continues to work
- ✓ All existing tests should pass

### 🔍 Optional: Add Diagnostics

Consider adding policy diagnostics to your startup logging:

```csharp
var host = builder.Build();

// Log effective cache policies at startup
var diagnostics = host.Services.GetRequiredService<PolicyDiagnosticsService>();
var logger = host.Services.GetRequiredService<ILogger<Program>>();

logger.LogInformation("Effective cache policies:");
foreach (var policy in diagnostics.GetAllPolicies())
{
    logger.LogInformation(
        "  {MethodId}: Duration={Duration}, Sources={Sources}",
        policy.MethodId,
        policy.Policy.Duration,
        string.Join(", ", policy.Contributions.Select(c => c.SourceId)));
}
```

### 🧪 Optional: Update Tests

If you have tests that directly manipulate `IMethodCacheConfiguration`, they should continue to work. However, consider testing via the policy registry for better accuracy:

```csharp
// Old way (still works)
var config = serviceProvider.GetRequiredService<IMethodCacheConfiguration>();
var settings = config.GetMethodSettings("MyApp.IUserService.GetUserAsync");
Assert.Equal(TimeSpan.FromMinutes(10), settings.Duration);

// New way (more accurate - reflects actual runtime behavior)
var registry = serviceProvider.GetRequiredService<IPolicyRegistry>();
var result = registry.GetPolicy("MyApp.IUserService.GetUserAsync");
Assert.Equal(TimeSpan.FromMinutes(10), result.Policy.Duration);
```

## Troubleshooting

### Priority Not Working as Expected?

Use `PolicyDiagnosticsService` to see which sources are contributing:

```csharp
var diagnostics = serviceProvider.GetRequiredService<PolicyDiagnosticsService>();
var policy = diagnostics.GetPolicy("YourMethodId");

foreach (var contribution in policy.Contributions.OrderByDescending(c => c.Timestamp))
{
    Console.WriteLine($"{contribution.SourceId} (priority from source): {contribution.Fields}");
}
```

### Configuration Not Taking Effect?

1. Check if source generator ran (look for generated `GeneratedPolicyRegistrations` class)
2. Verify your interface has `[Cache]` attributes
3. Ensure generated DI extension methods are being called
4. Use `PolicyDiagnosticsService.GetPolicy()` to inspect effective configuration

### Runtime Overrides Not Working?

Runtime overrides now use `RuntimeOverridePolicySource` with priority 100. Verify it's registered:

```csharp
// Should see RuntimeOverridePolicySource in the output
var diagnostics = serviceProvider.GetRequiredService<PolicyDiagnosticsService>();
foreach (var policy in diagnostics.GetAllPolicies())
{
    var hasRuntimeOverride = policy.Contributions.Any(c => c.SourceId == "runtime-overrides");
    if (hasRuntimeOverride)
    {
        Console.WriteLine($"{policy.MethodId} has runtime override");
    }
}
```

## Breaking Changes

**None!** This is a non-breaking internal refactoring. All public APIs remain compatible.

## Advanced: Internal Changes

For developers extending MethodCache or working on the codebase:

### Architecture Changes

1. **Policy Sources**: All configuration sources now implement `IPolicySource`
   - `GeneratedAttributePolicySource` (from source generator)
   - `FluentPolicySource` (wraps `MethodCacheConfiguration`)
   - `ConfigurationManagerPolicySource` (JSON/YAML)
   - `RuntimeOverridePolicySource` (runtime overrides)

2. **Policy Resolution**: `PolicyResolver` merges policies from all sources based on priority

3. **Decorator Injection**: Generated decorators now inject:
   - `IPolicyRegistry` (instead of `IServiceProvider`)
   - `ICacheKeyGenerator` (instead of resolving it)
   - `ICacheManager` (same as before)

4. **Runtime Lookup**: Decorators call `_policyRegistry.GetPolicy(methodId)` at runtime

### Generated Code Changes

Generated decorators now look like:

```csharp
public class UserServiceDecorator : IUserService
{
    private readonly IUserService _decorated;
    private readonly ICacheManager _cacheManager;
    private readonly IPolicyRegistry _policyRegistry;  // NEW
    private readonly ICacheKeyGenerator _keyGenerator; // NEW

    public async Task<User> GetUserAsync(int userId)
    {
        var policyResult = _policyRegistry.GetPolicy("MyApp.IUserService.GetUserAsync");
        var settings = CachePolicyConversion.ToCacheMethodSettings(policyResult.Policy);

        return await _cacheManager.GetOrCreateAsync<User>(
            "GetUserAsync",
            new object[] { userId },
            async () => await _decorated.GetUserAsync(userId),
            settings,
            _keyGenerator,
            settings.IsIdempotent);
    }
}
```

## See Also

- [Configuration Guide](../user-guide/CONFIGURATION_GUIDE.md) - Complete configuration reference
- [SampleApp](../../MethodCache.SampleApp/) - Working example with diagnostics
- [Policy Pipeline Implementation Plan](../developer/POLICY_PIPELINE_IMPLEMENTATION_PLAN.md) - Architecture details
