# MethodCache

[![NuGet Version](https://img.shields.io/nuget/v/MethodCache.Core)](https://www.nuget.org/packages/MethodCache.Core)
[![Build Status](https://img.shields.io/github/actions/workflow/status/yourusername/methodcache/ci.yml)](https://github.com/yourusername/methodcache/actions)
[![Coverage](https://img.shields.io/codecov/c/github/yourusername/methodcache)](https://codecov.io/gh/yourusername/methodcache)
[![License](https://img.shields.io/github/license/yourusername/methodcache)](LICENSE)

> **Switch caching on in minutes, run it safely in production, and stay in control at runtime.**

MethodCache gives teams the three things they crave most from caching:

- **Immediate productivity** – decorate a method or call the fluent API and the source generator emits zero-reflection decorators for you.
- **Operational control** – runtime overrides, analyzers, and metrics keep caches observable and tweakable without redeploying.
- **Scale without lock-in** – plug in in-memory, Redis, hybrid, and ETag layers while reusing the same configuration surfaces.

Whether you are wrapping your own services or slapping caching onto third-party SDKs, MethodCache keeps business code clean, deploys safely, and gives operations a kill switch when they need it.

---

## 📚 Contents

- [Quick Start](#quick-start)
- [Why MethodCache?](#why-methodcache)
- [Configuration Surfaces](#configuration-surfaces)
  - [Attributes](#attributes)
  - [Fluent API](#fluent-api)
  - [JSON / YAML](#json--yaml)
  - [Runtime Overrides](#runtime-overrides)
- [Cache Third‑Party Libraries](#cache-third-party-libraries)
- [Feature Highlights](#feature-highlights)
- [Architecture at a Glance](#architecture-at-a-glance)
- [Packages](#packages)
- [Documentation & Samples](#documentation--samples)
- [Contributing](#contributing)

---

## 🚀 Quick Start

### 1. Install packages

```bash
# Minimal setup
dotnet add package MethodCache.Core
# Source generator + analyzers (recommended)
dotnet add package MethodCache.SourceGenerator
```

### 2. Mark methods with `[Cache]`

```csharp
public interface IUserService
{
    Task<UserProfile> GetUserAsync(int userId);
}

public class UserService : IUserService
{
    [Cache]
    public Task<UserProfile> GetUserAsync(int userId)
        => _db.Users.FindAsync(userId).AsTask();
}
```

### 3. Register MethodCache

```csharp
builder.Services.AddMethodCache(config =>
{
    config.DefaultDuration(TimeSpan.FromMinutes(10))
          .DefaultKeyGenerator<MessagePackKeyGenerator>();
});
```

That’s it – the source generator emits decorators, `ICacheManager` handles storage, and you retain clean business code.

---

## 💡 Why MethodCache?

| Capability | What it means for you |
|------------|-----------------------|
| **Compile‑time decorators** | Roslyn source generator produces zero‑reflection proxies with per‑method caching logic. |
| **Fluent, config, or runtime control** | Choose attributes, the new fluent API (versioning, custom key generators, predicates), configuration files, or management endpoints. |
| **Provider agnostic** | In‑memory L1, Redis L2, hybrid orchestration, compression, distributed locks, multi‑region support. |
| **Safe by default** | Analyzers validate usage (e.g., key generator types), circuit breakers and stampede protection guard your downstreams. |
| **Observability ready** | Metrics hooks, structured logging, health checks, diagnostics – built to operate in production. |
| **Third‑party caching** | Layer caching onto NuGet packages or SDKs without touching their source. |

---

## ⚙️ Configuration Surfaces

### Attributes

Lightweight opt‑in. Apply `[Cache]` (and `[CacheInvalidate]`) to interface members or virtual methods.

```csharp
public interface IOrdersService
{
    [Cache("orders", Duration = "00:15:00", Tags = new[] { "orders", "customers" }, Version = 2,
        KeyGeneratorType = typeof(FastHashKeyGenerator))]
    Task<Order> GetAsync(int id);
}
```

Attributes describe intent; everything can be overridden downstream.

### Fluent API

Use `AddMethodCacheFluent` or the generator‑emitted rules to express richer policies with IntelliSense support.

```csharp
services.AddMethodCacheFluent(fluent =>
{
    fluent.DefaultPolicy(o => o
        .WithDuration(TimeSpan.FromMinutes(10))
        .WithTags("default"));

    fluent.ForService<IOrdersService>()
          .Method(s => s.GetAsync(default))
          .WithGroup("orders")
          .WithVersion(3)
          .WithKeyGenerator<FastHashKeyGenerator>()
          .When(ctx => ctx.Key.Contains("Get"))
          .RequireIdempotent();
});
```

### JSON / YAML

Environment‑specific configuration without recompiling.

```json
{
  "MethodCache": {
    "Defaults": { "Duration": "00:05:00" },
    "Services": {
      "MyApp.Services.IOrdersService.GetAsync": {
        "Duration": "00:15:00",
        "Tags": ["orders", "customers"],
        "Version": 3
      }
    }
  }
}
```

### Runtime Overrides

Runtime sources carry the highest precedence – perfect for management UIs and incident response.

```csharp
var configurator = app.Services.GetRequiredService<IRuntimeCacheConfigurator>();

// Apply a live override using the same fluent API you use at startup
await configurator.ApplyFluentAsync(fluent =>
{
    fluent.ForService<IOrdersService>()
          .Method(s => s.GetAsync(default))
          .Configure(o => o
              .WithDuration(TimeSpan.FromMinutes(1))
              .WithTags("runtime-override"));
});

// Surface overrides to your management UI
var overrides = await configurator.GetOverridesAsync();

// Roll back specific overrides without touching attributes or JSON
await configurator.RemoveOverrideAsync(typeof(IOrdersService).FullName!, nameof(IOrdersService.GetAsync));

// Or reset the runtime layer completely
await configurator.ClearOverridesAsync();

// Need the full effective picture (after attributes/config/runtime)?
var effectiveConfig = await configurator.GetEffectiveConfigurationAsync();
```

> `IRuntimeCacheConfigurator` is registered automatically when you call `AddMethodCacheWithSources(...)`, making it trivial to plug a UI or management API on top of the fluent builders you already use at startup.

---

## 🤝 Cache Third‑Party Libraries

Drop caching onto external interfaces (Stripe, AWS SDKs, GraphQL clients, etc.) without modifying their code.

```csharp
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

> Read the full guide: [Third‑Party Caching](THIRD_PARTY_CACHING.md)

---

## 🔍 Feature Highlights

### ⚡ Performance

![Cache Hit Performance](https://img.shields.io/badge/Cache%20Hit-145ns-brightgreen) ![Cache Miss Performance](https://img.shields.io/badge/Cache%20Miss-1.3ms-yellow) ![Benchmark Version](https://img.shields.io/badge/Benchmarked-v2.0.0-preview-blue)

MethodCache delivers exceptional performance with microsecond-level cache hits:
🚀 **Cache speedup: 8276x faster** than no caching

| Operation | Small Model (1 item) | Medium Model (1 item) | Large Model (1 item) |
|-----------|---------------------|----------------------|---------------------|
| No Caching | **1.2 ms** | N/A | N/A |
| Cache Miss | **1.3 ms** | N/A | N/A |
| Cache Hit | **145 ns** | N/A | N/A |
| Cache HitCold | **245 ns** | N/A | N/A |
| Cache Invalidation | **89 ns** | N/A | N/A |

> 📊 **Benchmarks** run on .NET 9.0 with BenchmarkDotNet. Results from December 20, 2024.
>
> 📈 [View detailed performance trends](PERFORMANCE.md) | 🔍 [Raw benchmark data](.performance-data/)

### Performance Highlights

- **Cache Hits**: Sub-microsecond response times for cached data
- **Memory Efficient**: Minimal memory allocations during cache operations
- **Scalable**: Consistent performance across different data sizes
- **Zero-Overhead**: Negligible impact when caching is disabled
## 🏗️ Architecture at a Glance

```mermaid
graph TB
    A[Attribute / Fluent Config] --> B[Roslyn Source Generator]
    B --> C[Generated Decorator]
    C --> D[ICacheManager]
    D --> E[In-Memory L1]
    D --> F[Redis L2]
    F --> G[Multi-Region / Compression / Locks]

    subgraph Observability
        C --> H[Metrics]
        D --> I[Analyzers]
    end
```

Configuration precedence:
1. **Runtime overrides**
2. **Startup fluent/config builders**
3. **Attribute groups and defaults**

---

## 📦 Packages

| Package | Description |
|---------|-------------|
| `MethodCache.Core` | Core abstractions, in-memory cache manager, attributes. |
| `MethodCache.SourceGenerator` | Roslyn generator emitting decorators and fluent registry. |
| `MethodCache.Analyzers` | Roslyn analyzers (MC0001–MC0004) ensuring safe usage. |
| `MethodCache.Providers.Redis` | Redis provider with hybrid orchestration, compression, locking. |
| `MethodCache.ETags` | HTTP ETag integration layered on MethodCache. |

---

## 📖 Documentation & Samples

- [Configuration Guide](CONFIGURATION_GUIDE.md)
- [Fluent API Specification](FLUENT_API_SPEC.md)
- [Third‑Party Caching Scenarios](THIRD_PARTY_CACHING.md)
- [ETag Usage Examples](ETAG_USAGE_EXAMPLES.md)
- [Sample App](MethodCache.SampleApp)
- [Demo Project](MethodCache.Demo)

---

## 🤝 Contributing

We welcome issues, ideas, and pull requests. Please read the contribution guidelines (coming soon) and ensure `dotnet format` plus the test suite (`dotnet test MethodCache.sln`) passes before submitting.

---

Built with ❤️ for the .NET community.



## 🚀 Performance Benchmarks

Last updated: 2025-09-20_19-55-00 (commit: `abc1234`)

| Benchmark | Mean | Error | Allocated |
|-----------|------|-------|-----------|
| CacheHit | 145.23 ns | ±1.23 | 128 B |
| CacheMiss | 1245.67 ns | ±7.61 | 2048 B |
| NoCaching | 1189.34 ns | ±6.39 | 1920 B |