# MethodCache

**Switch caching on in minutes, run it safely in production, and stay in control at runtime.**

MethodCache gives teams the three things they crave most from caching:

- ⚡ **Immediate productivity** – decorate a method or call the fluent API and the source generator emits zero-reflection decorators for you
- 🎛️ **Operational control** – runtime overrides, analyzers, and metrics keep caches observable and tweakable without redeploying
- 🚀 **Scale without lock-in** – plug in in-memory, Redis, hybrid, and ETag layers while reusing the same configuration surfaces

Whether you are wrapping your own services or slapping caching onto third-party SDKs, MethodCache keeps business code clean, deploys safely, and gives operations a kill switch when they need it.

## 🚀 Quick Start

### 1. Install packages

```bash
dotnet add package MethodCache.Core
dotnet add package MethodCache.SourceGenerator
```

### 2. Mark methods with `[Cache]`

```csharp
public interface IUserService
{
    [Cache]
    Task<UserProfile> GetUserAsync(int userId);
}
```

### 3. Register MethodCache

```csharp
builder.Services.AddMethodCache(config =>
{
    config.DefaultDuration(TimeSpan.FromMinutes(10))
          .DefaultKeyGenerator<FastHashKeyGenerator>();
});
```

That's it! The source generator emits decorators, `ICacheManager` handles storage, and you retain clean business code.

## ✨ Method Chaining API

For non-generated scenarios or third-party libraries:

```csharp
var user = await cache.Cache(() => userService.GetUserAsync(userId))
    .WithDuration(TimeSpan.FromHours(1))
    .WithTags("user", $"user:{userId}")
    .WithKeyGenerator<FastHashKeyGenerator>()
    .ExecuteAsync();
```

Perfect for caching external APIs, legacy code, or when you prefer explicit control.

## 🎯 Key Features

### Performance
- **Cache Hit**: 145ns
- **Cache Miss**: 1.3ms
- **8276x faster** than no caching

### Key Generators
| Generator | Performance | Use Case |
|-----------|------------|----------|
| `FastHashKeyGenerator` | ~50ns | Production high-throughput |
| `JsonKeyGenerator` | ~200ns | Development/debugging |
| `MessagePackKeyGenerator` | ~100ns | Complex objects |

### Configuration
- **Attributes** – Declarative, compile-time validated
- **Fluent API** – Type-safe, IntelliSense-guided
- **JSON/YAML** – Environment-specific, no recompilation
- **Runtime Overrides** – Live changes without deployment

### Cache Management
```csharp
// Tag-based invalidation
await cacheManager.InvalidateByTagsAsync("users", "profiles");

// Pattern-based invalidation
await cacheManager.InvalidateByTagPatternAsync("user:*");

// Exact key invalidation
await cacheManager.InvalidateByKeysAsync("GetUser_123");
```

## 📦 Packages

| Package | Purpose |
|---------|---------|
| `MethodCache.Core` | Core abstractions, in-memory manager |
| `MethodCache.SourceGenerator` | Roslyn generator (recommended) |
| `MethodCache.Analyzers` | Compile-time validation |
| `MethodCache.Providers.Redis` | Redis L2 distributed caching |
| `MethodCache.ETags` | HTTP ETag integration |

## 🔧 Common Scenarios

### Basic Read Operation
```csharp
[Cache("users",
    Duration = "00:30:00",
    Tags = new[] { "users", "user:{userId}" },
    RequireIdempotent = true)]
Task<User> GetUserAsync(int userId);
```

### Automatic Invalidation
```csharp
[CacheInvalidate(Tags = new[] { "users", "user:{userId}" })]
Task UpdateUserAsync(int userId, UserUpdateDto update);
```

### Third-Party Library Caching
```json
{
  "MethodCache": {
    "Services": {
      "WeatherApi.Client.IWeatherApiClient.GetCurrentWeatherAsync": {
        "Duration": "00:05:00",
        "Tags": ["weather", "external-api"]
      }
    }
  }
}
```

### Cache Versioning
```csharp
// Increment version to invalidate all existing cache entries
[Cache(Version = 2)]
Task<UserDto> GetUserAsync(int id);
```

## 📚 Documentation

- **Full Documentation**: [docs.methodcache.io](https://github.com/eoniclabs/MethodCache/blob/main/README.md)
- **Configuration Guide**: [CONFIGURATION_GUIDE.md](https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/CONFIGURATION_GUIDE.md)
- **Fluent API Reference**: [FLUENT_API.md](https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/FLUENT_API.md)
- **Third-Party Caching**: [THIRD_PARTY_CACHING.md](https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/THIRD_PARTY_CACHING.md)

## 🎓 Best Practices

1. **Use `FastHashKeyGenerator` for production**, `JsonKeyGenerator` for debugging
2. **Tag all cache entries** for flexible invalidation
3. **Set `RequireIdempotent = true`** for read operations
4. **Coordinate tags** between `[Cache]` and `[CacheInvalidate]`
5. **Choose duration based on data volatility**:
   - Static data: 1-24 hours
   - Occasional updates: 5-30 minutes
   - Frequent changes: 30 seconds - 5 minutes

## ⚠️ What NOT to Cache

- Methods with side effects (unless paired with invalidation)
- Security-sensitive operations
- Operations that modify state
- Real-time data without short durations
- Methods returning `IDisposable` resources

## 🐛 Troubleshooting

### Cache always misses?
- Verify `services.AddMethodCache()` is called
- Check `Duration` is set
- Ensure method is virtual or interface member

### Seeing stale data?
- Reduce `Duration` value
- Add `Tags` and invalidate on writes
- Increment `Version` to invalidate all entries

### Performance issues?
- Switch to `FastHashKeyGenerator`
- Review cache duration (too short = frequent misses)
- Consider Redis L2 for distributed scenarios

## 🤝 Contributing

We welcome issues, ideas, and pull requests!

- **Issues**: [github.com/eoniclabs/MethodCache/issues](https://github.com/eoniclabs/MethodCache/issues)
- **Repository**: [github.com/eoniclabs/MethodCache](https://github.com/eoniclabs/MethodCache)

## 📄 License

See [LICENSE](https://github.com/eoniclabs/MethodCache/blob/main/LICENSE) file.

---

Built with ❤️ for the .NET community by [Eonic Labs](https://github.com/eoniclabs)