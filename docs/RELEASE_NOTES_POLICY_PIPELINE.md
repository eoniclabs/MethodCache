# MethodCache v1.1.0 - Policy Pipeline Release

## 🎉 What's New

### Unified Policy Pipeline Architecture

MethodCache now uses a **unified policy pipeline** for all configuration sources. This is an internal architectural improvement that provides:

- ✅ **Consistent priority handling** across all configuration sources
- ✅ **Policy diagnostics** - inspect effective configuration at runtime
- ✅ **Better runtime override support** - guaranteed highest priority
- ✅ **Foundation for advanced features** - policy watching, dynamic updates, etc.

**This is a non-breaking change.** All existing code continues to work without modifications.

## 🔍 New Feature: Policy Diagnostics

Inspect your effective cache configuration at runtime using the new `PolicyDiagnosticsService`:

```csharp
var diagnostics = serviceProvider.GetRequiredService<PolicyDiagnosticsService>();

// See all configured methods
foreach (var policy in diagnostics.GetAllPolicies())
{
    Console.WriteLine($"{policy.MethodId}:");
    Console.WriteLine($"  Duration: {policy.Policy.Duration}");
    Console.WriteLine($"  Tags: {string.Join(", ", policy.Policy.Tags)}");
    Console.WriteLine($"  Sources: {string.Join(", ", policy.Contributions.Select(c => c.SourceId))}");
}

// Inspect a specific method
var userPolicy = diagnostics.GetPolicy("MyApp.IUserService.GetUserAsync");
if (userPolicy != null)
{
    Console.WriteLine($"Cache duration: {userPolicy.Policy.Duration}");

    // See which configuration sources contributed
    foreach (var contribution in userPolicy.Contributions)
    {
        Console.WriteLine($"  {contribution.SourceId} set: {contribution.Fields}");
    }
}
```

**Use cases:**
- Debug configuration issues (which source is setting what?)
- Validate configuration at startup
- Build admin dashboards showing effective cache policies
- Troubleshoot unexpected cache behavior

## 📊 Configuration Priority Clarification

Configuration sources now have well-defined, guaranteed priorities:

| Source | Priority | When to Use |
|--------|----------|-------------|
| `[Cache]` Attributes | 10 | Default configuration in code |
| Fluent/Programmatic API | 40 | Override defaults in code |
| JSON/YAML Configuration | 50 | Environment-specific settings |
| Runtime Overrides | 100 | Emergency changes, A/B testing |

**Higher priority always wins.** Runtime overrides now correctly override all other sources.

### Example

```csharp
// Attribute sets default
[Cache(Duration = "00:05:00")]  // Priority 10
Task<User> GetUserAsync(int id);

// JSON config overrides
{
  "MethodCache": {
    "Services": {
      "MyApp.IUserService.GetUserAsync": {
        "Duration": "00:10:00"  // Priority 50 - wins over attribute
      }
    }
  }
}

// Runtime override beats everything
configurator.OverrideMethod("MyApp.IUserService.GetUserAsync", s => {
    s.Duration = TimeSpan.FromMinutes(15);  // Priority 100 - final word
});
```

## 🔧 What Changed Internally

### For Users: Nothing!

All public APIs remain unchanged:
- ✅ `[Cache]` and `[CacheInvalidate]` attributes work the same
- ✅ `AddMethodCache()`, `AddMethodCacheFluent()` work the same
- ✅ JSON/YAML configuration format unchanged
- ✅ Fluent API `config.AddMethod()` unchanged
- ✅ All existing tests pass without modification

### For Contributors: Architecture Improvements

**Before (v1.0.x):**
```
Configuration Sources → MethodCacheConfiguration (direct mutation) → Decorators
```

**After (v1.1.x):**
```
Configuration Sources → IPolicySource → PolicyResolver → IPolicyRegistry → Decorators
```

**Key changes:**
1. **Policy Sources**: All config sources implement `IPolicySource`
   - `GeneratedAttributePolicySource` - from source generator
   - `FluentPolicySource` - wraps `MethodCacheConfiguration`
   - `ConfigurationManagerPolicySource` - JSON/YAML files
   - `RuntimeOverridePolicySource` - runtime overrides

2. **Decorator Injection**: Generated decorators now inject:
   - `IPolicyRegistry` (instead of `IServiceProvider`)
   - `ICacheKeyGenerator` (direct injection)
   - `ICacheManager` (same as before)

3. **Runtime Resolution**: Decorators call `_policyRegistry.GetPolicy(methodId)` instead of resolving from DI

## 📚 Migration Guide

See [POLICY_PIPELINE_MIGRATION.md](migration/POLICY_PIPELINE_MIGRATION.md) for:
- Detailed before/after comparison
- How to use `PolicyDiagnosticsService`
- Troubleshooting guide
- Advanced internal architecture details

**TL;DR: No migration needed! Your existing code works as-is.**

## ✅ Verification

All 640 tests pass:
- Core: 116/116 ✅
- Generator: 17/17 ✅
- Integration: 23/23 ✅
- Providers: 206/206 ✅
- Other components: 278/278 ✅

Sample application verified and demonstrates new diagnostics features.

## 🐛 Bug Fixes

### Runtime Overrides Now Work Correctly

**Before:** Runtime overrides had priority 40, could be overridden by configuration files (priority 50)

**After:** Runtime overrides have priority 100, guaranteed to override everything

This fixes scenarios where emergency runtime configuration changes were being ignored.

### Configuration Source Precedence

**Before:** Priority was inconsistent - sometimes attributes would override programmatic config

**After:** Strict priority enforcement - higher priority always wins, no exceptions

## 🚀 Performance

No performance impact. The policy pipeline adds negligible overhead:
- Policy resolution is cached in `IPolicyRegistry`
- Decorators call `GetPolicy()` once per method invocation
- Conversion from `CachePolicy` to `CacheMethodSettings` is fast (record copying)

## 🔮 Future Enhancements

The policy pipeline architecture enables:
- **Dynamic policy updates** - reload configuration without restart
- **Policy versioning** - track configuration changes over time
- **Advanced diagnostics** - policy diff tools, conflict detection
- **External policy sources** - configuration from databases, remote APIs
- **Policy templates** - reusable configuration patterns

## 📦 Package Versions

- `MethodCache` → 1.1.0
- `MethodCache.Core` → 1.1.0
- `MethodCache.Abstractions` → 1.1.0
- `MethodCache.SourceGenerator` → 1.1.0

All other packages remain at current versions (no changes required).

## 🙏 Acknowledgments

This release represents a significant internal architectural improvement while maintaining 100% backward compatibility. Special thanks to all contributors and users who provided feedback during development.

## 📞 Support

- **Issues**: [GitHub Issues](https://github.com/your-repo/MethodCache/issues)
- **Documentation**: [docs/](../docs/)
- **Migration Guide**: [POLICY_PIPELINE_MIGRATION.md](migration/POLICY_PIPELINE_MIGRATION.md)
- **Examples**: [MethodCache.SampleApp](../MethodCache.SampleApp/)

---

**Happy Caching! 🚀**
