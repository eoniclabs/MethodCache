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
  - [Fluent API Caching](#fluent-api-caching)
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
builder.Services.AddMethodCache(config =>
{
    config.DefaultDuration(TimeSpan.FromMinutes(10))
          .DefaultKeyGenerator<FastHashKeyGenerator>();
});
```

That is enough to enable source-generated cache decorators for attributed methods.

## Feature Overview

| Feature | Benefit | How to use |
|---------|---------|------------|
| Source-generated decorators | Removes runtime reflection and keeps business methods clean | Add `[Cache]` on interface methods and register MethodCache |
| Fluent API | Cache methods without attributes or without changing external code | Use `ICacheManager.Cache(...).WithDuration(...).ExecuteAsync()` |
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

### Fluent API Caching

Use the fluent API when attributes are not practical (for example, third-party clients).

```csharp
var user = await cache.Cache(() => userService.GetUserAsync(userId))
    .WithDuration(TimeSpan.FromMinutes(30))
    .WithTags("users", $"user:{userId}")
    .WithKeyGenerator<JsonKeyGenerator>()
    .ExecuteAsync();
```

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
await cacheManager.InvalidateByKeysAsync("users:123");
await cacheManager.InvalidateByTagsAsync("users", "tenant:42");
await cacheManager.InvalidateByTagPatternAsync("user:*");
```

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

Provider note: pattern support is provider-dependent. `InMemoryCacheManager` supports the wildcard syntax above. `HybridCacheManager` currently logs a warning and treats `InvalidateByTagPatternAsync` as a no-op.

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

1. Fluent API in your wrapper/service code (most direct for application developers).
2. Central JSON/YAML policy configuration (useful for centralized operations control).

Fluent API example:

```csharp
public sealed class WeatherFacade
{
    private readonly IWeatherApiClient _client;
    private readonly ICacheManager _cache;

    public Task<WeatherResponse> GetCurrentAsync(string city) =>
        _cache.Cache(() => _client.GetCurrentAsync(city))
              .WithDuration(TimeSpan.FromMinutes(5))
              .WithTags("weather", $"city:{city}")
              .ExecuteAsync();
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
| `MethodCache.Providers.Memory` | Advanced in-memory provider |
| `MethodCache.OpenTelemetry` | Tracing and metrics integration |

### Experimental

| Package | Purpose |
|---------|---------|
| `MethodCache.Providers.SqlServer` | SQL Server-backed cache provider |
| `MethodCache.HttpCaching` | HTTP response caching middleware |

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
- [Method Chaining Examples](docs/examples/basic-usage/method_chaining_examples.cs)
- [Sample App](MethodCache.SampleApp/)
- [Demo Project](MethodCache.Demo/)

## Contributing

Issues and pull requests are welcome.

Before submitting a change, run:

```bash
dotnet format
dotnet test MethodCache.sln
```
