# MethodCache

MethodCache provides declarative and fluent caching APIs for .NET applications.

Use it to:

- Add caching with attributes and source-generated decorators.
- Apply caching to existing or third-party services with a fluent API.
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
builder.Services.AddMethodCache(config =>
{
    config.DefaultDuration(TimeSpan.FromMinutes(10))
          .DefaultKeyGenerator<FastHashKeyGenerator>();
});
```

## Fluent API Example

```csharp
var profile = await cache.Cache(() => userService.GetUserAsync(userId))
    .WithDuration(TimeSpan.FromMinutes(30))
    .WithTags("users", $"user:{userId}")
    .ExecuteAsync();
```

## Feature Summary

| Feature | Why it is useful |
|---------|------------------|
| Source generation | Keeps runtime overhead low and removes reflection-based interception |
| Multiple config sources | Combine code defaults, file config, and runtime overrides |
| Tag-based invalidation | Invalidate related entries without managing each key |
| Runtime overrides | Adjust behavior during incidents or controlled rollouts |
| Provider flexibility | Start with in-memory; add Redis or advanced providers later |
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
| `MethodCache.Providers.Redis` | Redis provider |
| `MethodCache.Providers.Memory` | Advanced memory provider |
| `MethodCache.OpenTelemetry` | Observability integration |

### Experimental

| Package | Purpose |
|---------|---------|
| `MethodCache.Providers.SqlServer` | SQL Server provider |

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
