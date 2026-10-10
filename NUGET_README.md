# MethodCache

MethodCache provides declarative and fluent caching APIs for .NET applications.

Use it to:

- Add caching with attributes and source-generated decorators.
- Cache any call, including third-party clients, with an explicit key through `ICacheManager`.
- Invalidate by tags, keys, or patterns.
- Change cache policies at runtime without redeploying.

## Quick Start

### 1. Install

```bash
dotnet add package MethodCache
```

This package includes `MethodCache.Core`, `MethodCache.SourceGenerator`, and `MethodCache.Analyzers`.

### 2. Add attributes

```csharp
public interface IUserService
{
    [Cache(Duration = "00:10:00", Tags = new[] { "users", "user:{userId}" })]
    Task<UserProfile> GetUserAsync(int userId);

    [CacheInvalidate(Tags = new[] { "users", "user:{userId}" })]
    Task UpdateUserAsync(int userId, UserUpdate update);
}
```

### 3. Register MethodCache

```csharp
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddMethodCache();
```

`AddMethodCache()` wraps `IUserService` in its generated cache decorator and keeps the lifetime you chose. Call it after your own registrations: a service registered later is not cached.

You can also skip registering the service yourself. `AddMethodCache()` then registers `UserService` as a singleton, provided it is in the same project as `IUserService`. If the interface lives in another project, pass that project's assembly:

```csharp
builder.Services.AddMethodCache(configure: null, typeof(IUserService).Assembly);
```

Registration uses no reflection over your types, so it works with trimming and Native AOT.

## Manual Caching Example

```csharp
var profile = await cache.GetOrCreateAsync(
    $"user:{userId}",
    async (ctx, ct) => await userService.GetUserAsync(userId),
    o => o.WithDuration(TimeSpan.FromMinutes(30))
          .WithTags("users", $"user:{userId}"));
```

## Feature Summary

| Feature | Why it is useful |
|---------|------------------|
| Source generation | Keeps runtime overhead low and removes reflection-based interception |
| Multiple config sources | Combine attributes, startup code, appsettings.json (`AddMethodCacheFromConfiguration`) and runtime overrides |
| Tag-based invalidation | Invalidate related entries without managing each key |
| Runtime overrides | Adjust behavior during incidents or controlled rollouts |
| Provider flexibility | Start with in-memory; add Redis, SQL Server or any `IDistributedCache` later |
| Analyzer support | Catch caching mistakes during build |

## Common Usage Patterns

### Raw key parameter

```csharp
[Cache(Duration = "00:10:00")]
Task<T> GetAsync<T>([CacheKey(UseAsRawKey = true)] string cacheKey);
```

### Invalidate by tags

```csharp
await cacheManager.InvalidateByTagsAsync("users", "tenant:42");
```

### Runtime override

```csharp
var configurator = app.Services.GetRequiredService<IRuntimeCacheConfigurator>();

await configurator.ApplyAsync(fluent =>
{
    fluent.ForService<IUserService>()
          .Method(s => s.GetUserAsync(default))
          .Configure(o => o.WithDuration(TimeSpan.FromMinutes(2)));
});
```

## Packages

### Stable

| Package | Purpose |
|---------|---------|
| `MethodCache` | Meta-package |
| `MethodCache.Core` | Core abstractions and in-memory manager |
| `MethodCache.SourceGenerator` | Roslyn source generator |
| `MethodCache.Analyzers` | Compile-time validation |

### Beta

| Package | Purpose |
|---------|---------|
| `MethodCache.Providers.Redis` | Redis as the distributed cache, with a pub/sub backplane |
| `MethodCache.OpenTelemetry` | Observability integration |

### Experimental

| Package | Purpose |
|---------|---------|
| `MethodCache.Providers.SqlServer` | SQL Server as the distributed cache, with a polling backplane |
| `MethodCache.AspNetCore` | Admin endpoints and health check |

## Documentation

- [Repository README](https://github.com/eoniclabs/MethodCache/blob/main/README.md)
- [Configuration Guide](https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/CONFIGURATION_GUIDE.md)
- [Fluent API Guide](https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/FLUENT_API.md)
- [Third-party Caching Guide](https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/THIRD_PARTY_CACHING.md)
- [Benchmarks](https://github.com/eoniclabs/MethodCache/tree/main/MethodCache.Benchmarks)

## Contributing

- Issues: [github.com/eoniclabs/MethodCache/issues](https://github.com/eoniclabs/MethodCache/issues)
- Repository: [github.com/eoniclabs/MethodCache](https://github.com/eoniclabs/MethodCache)

## License

See [LICENSE](https://github.com/eoniclabs/MethodCache/blob/main/LICENSE).
