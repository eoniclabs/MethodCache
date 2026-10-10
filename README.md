# MethodCache

[![NuGet Version](https://img.shields.io/badge/NuGet%20Version-not%20published-lightgrey)](https://www.nuget.org/packages?q=MethodCache)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512BD4)](https://dotnet.microsoft.com/)
[![Build Status](https://github.com/eoniclabs/MethodCache/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/eoniclabs/MethodCache/actions/workflows/ci.yml)
[![Coverage](https://codecov.io/gh/eoniclabs/MethodCache/graph/badge.svg?branch=main)](https://codecov.io/gh/eoniclabs/MethodCache)
[![License](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

MethodCache is a .NET caching library that supports two usage styles:

- Declarative caching with attributes and source-generated decorators.
- Explicit caching with a fluent runtime API.

It is designed for teams that want caching behavior to be easy to add, easy to review, and easy to change as production needs evolve.

## Contents

- [Quick Start](#quick-start)
- [Feature Overview](#feature-overview)
- [How To Use MethodCache](#how-to-use-methodcache)
  - [Attribute-based Caching](#attribute-based-caching)
  - [Manual Caching](#manual-caching)
  - [Configuration Sources](#configuration-sources)
  - [Invalidation](#invalidation)
  - [Runtime Overrides](#runtime-overrides)
  - [Third-party Library Caching](#third-party-library-caching)
- [Packages](#packages)
- [Benchmarks](#benchmarks)
- [CI and Coverage](#ci-and-coverage)
- [Documentation and Samples](#documentation-and-samples)
- [Contributing](#contributing)

## Quick Start

### 1. Install

```bash
dotnet add package MethodCache
```

The meta-package includes `MethodCache.Core`, `MethodCache.SourceGenerator`, and `MethodCache.Analyzers`.

### 2. Add caching attributes

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

You can also skip registering the service yourself. `AddMethodCache()` then registers `UserService` as a singleton, provided it is in the same project as `IUserService` (the source generator picks the class named like the interface, or the first one that implements it). If the interface lives in another project, pass that project's assembly:

```csharp
builder.Services.AddMethodCache(configure: null, typeof(IUserService).Assembly);
```

An implementation in a different project from its interface is never registered for you; register it yourself and `AddMethodCache()` decorates it.

Registration uses no reflection over your types, so it works with trimming and Native AOT.

That is enough to enable source-generated cache decorators for attributed methods.

## Feature Overview

| Feature | Benefit | How to use |
|---------|---------|------------|
| Source-generated decorators | Removes runtime reflection and keeps business methods clean | Add `[Cache]` on interface methods and register MethodCache |
| Manual caching | Cache calls without attributes or without changing external code | Use `ICacheManager.GetOrCreateAsync(key, factory, options)` |
| Multiple configuration sources | Keep defaults in code and adjust behavior per environment | Combine attributes, startup configuration, file-based config, and runtime overrides |
| Tag-based invalidation | Invalidate related entries without tracking keys manually | Tag entries in `[Cache]` and call invalidation APIs by tag |
| Runtime overrides | Change cache behavior without redeploying | Use `IRuntimeCacheConfigurator` to apply and remove overrides |
| Provider flexibility | Use in-memory only, distributed, or hybrid strategies | Start with default provider, then add Redis/advanced providers as needed |
| Analyzer support | Catch invalid or risky caching patterns at compile time | Install analyzers (included in `MethodCache`) |

## How To Use MethodCache

### Attribute-based Caching

Attributes are the shortest path for application services and repositories.

```csharp
public interface IOrdersService
{
    [Cache("orders", Duration = "00:15:00", Tags = new[] { "orders", "customer:{customerId}" })]
    Task<IReadOnlyList<Order>> GetByCustomerAsync(int customerId);
}
```

For precomputed keys, you can mark a parameter as the raw cache key:

```csharp
public interface ILookupService
{
    [Cache(Duration = "00:10:00")]
    Task<T> GetAsync<T>([CacheKey(UseAsRawKey = true)] string cacheKey);
}
```

Use raw keys only when your key naming is globally unique.

### Manual Caching

Use `GetOrCreateAsync` with an explicit key when attributes are not practical (for example, third-party clients).

```csharp
var user = await cache.GetOrCreateAsync(
    $"user:{userId}",
    async (ctx, ct) => await userService.GetUserAsync(userId),
    o => o.WithDuration(TimeSpan.FromMinutes(30))
          .WithTags("users", $"user:{userId}"));
```

You choose the key, so two calls share an entry only when their keys are equal.

### Configuration Sources

MethodCache supports layered configuration. Higher-priority sources override lower-priority ones:

1. Attributes
2. Startup code configuration
3. JSON/YAML configuration
4. Runtime overrides

This lets you keep safe defaults in code while still adapting behavior in operations.

### Invalidation

Use exact keys, tags, or tag patterns depending on your domain model.

```csharp
await cacheManager.InvalidateByKeysAsync(
    MethodCacheKeys.For(typeof(IUserService), nameof(IUserService.GetUserAsync), MethodCacheKeys.Part(123)));
await cacheManager.InvalidateByTagsAsync("users", "tenant:42");
await cacheManager.InvalidateByTagPatternAsync("user:*");
```

Generated keys have the form `{Service}.{Method}[:v{Version}]:{arguments}`, for example `MyApp.IUserService.GetUserAsync:123`. `MethodCacheKeys` builds the same keys, so you never have to guess the format. Arguments that are not simple values (records, classes, collections) are hashed, but the key still starts with `{Service}.{Method}`.

`InvalidateByTagPatternAsync` supports simple wildcard matching:

- `*` matches zero or more characters
- `?` matches exactly one character
- All other characters are treated as literals (for example `:`, `.`, `[`, `(`)
- Matching is full-tag (equivalent to anchoring the pattern to both start and end)

Examples:

```csharp
await cacheManager.InvalidateByTagPatternAsync("user:*");       // user:1, user:1:profile
await cacheManager.InvalidateByTagPatternAsync("tenant:42:*");  // all tenant:42:* tags
await cacheManager.InvalidateByTagPatternAsync("user:??");      // user:12, not user:123
await cacheManager.InvalidateByTagPatternAsync("*:profile");    // any tag ending with :profile
```

Provider note: pattern support is provider-dependent. `InMemoryCacheManager` supports the wildcard syntax above. `HybridCacheManager` does not support it yet and throws `NotSupportedException`.

### Runtime Overrides

Use runtime overrides for temporary operational changes (shorter durations, feature rollout, incident mitigation).

```csharp
var configurator = app.Services.GetRequiredService<IRuntimeCacheConfigurator>();

await configurator.ApplyAsync(fluent =>
{
    fluent.ForService<IOrdersService>()
          .Method(s => s.GetByCustomerAsync(default))
          .Configure(o => o.WithDuration(TimeSpan.FromMinutes(2)));
});
```

### Third-party Library Caching

You can cache third-party libraries in two ways:

1. Manual caching (`GetOrCreateAsync` with an explicit key) in your wrapper/service code (most direct for application developers).
2. Central JSON/YAML policy configuration (useful for centralized operations control).

Manual caching example:

```csharp
public sealed class WeatherFacade
{
    private readonly IWeatherApiClient _client;
    private readonly ICacheManager _cache;

    public ValueTask<WeatherResponse> GetCurrentAsync(string city) =>
        _cache.GetOrCreateAsync(
            $"weather:{city}",
            async (ctx, ct) => await _client.GetCurrentAsync(city),
            o => o.WithDuration(TimeSpan.FromMinutes(5))
                  .WithTags("weather", $"city:{city}"));
}
```

JSON/YAML policy example:

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

## Packages

Supported frameworks: .NET 8.0, .NET 9.0, .NET 10.0.

### Stable

| Package | Purpose |
|---------|---------|
| `MethodCache` | Meta-package with core, generator, and analyzers |
| `MethodCache.Core` | Core abstractions and default in-memory cache manager |
| `MethodCache.SourceGenerator` | Roslyn source generator for decorators |
| `MethodCache.Analyzers` | Compile-time validation for cache usage |
| `MethodCache.Abstractions` | Shared interfaces and policy contracts |

### Beta

| Package | Purpose |
|---------|---------|
| `MethodCache.Providers.Redis` | Redis provider |
| `MethodCache.OpenTelemetry` | Tracing and metrics integration |

### Experimental

| Package | Purpose |
|---------|---------|
| `MethodCache.Providers.SqlServer` | SQL Server-backed cache provider |
| `MethodCache.HttpCaching` | HTTP response caching middleware |
| `MethodCache.AspNetCore` | Admin endpoints and health check for inspecting and invalidating the cache at runtime |

## Benchmarks

Benchmark sources and reports are in [MethodCache.Benchmarks](MethodCache.Benchmarks/) and [BenchmarkDotNet.Artifacts](MethodCache.Benchmarks/BenchmarkDotNet.Artifacts/results/).

Use these to compare scenarios (hit, miss-and-set, concurrent access, stampede protection) in your own environment.

## CI and Coverage

- Workflow: `.github/workflows/ci.yml`
- Triggers: push and pull request to `main`
- Steps: restore, build (Release), test with `XPlat Code Coverage`, upload test-results artifact
- Coverage upload: Codecov via GitHub OIDC

To enable coverage badges in your fork or org:

1. Ensure the repository is connected in Codecov.
2. Run the CI workflow at least once on GitHub Actions.
3. If your Codecov setup requires it, add `CODECOV_TOKEN` in repository secrets.

## Documentation and Samples

- [Configuration Guide](docs/user-guide/CONFIGURATION_GUIDE.md)
- [Fluent API Guide](docs/user-guide/FLUENT_API.md)
- [Third-party Caching Guide](docs/user-guide/THIRD_PARTY_CACHING.md)
- [Migration: Adding Caching to Existing Service](docs/migration/ADDING_CACHING_TO_EXISTING_SERVICE.md)
- [Migration: From IMemoryCache](docs/migration/MIGRATION_FROM_IMEMORYCACHE.md)
- [Sample App](MethodCache.SampleApp/)
- [Demo Project](MethodCache.Demo/)

## Contributing

Issues and pull requests are welcome.

Before submitting a change, run:

```bash
dotnet format
dotnet test MethodCache.sln
```
