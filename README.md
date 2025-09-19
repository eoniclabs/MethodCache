# MethodCache

[![NuGet Version](https://img.shields.io/nuget/v/MethodCache.Core)](https://www.nuget.org/packages/MethodCache.Core)
[![Build Status](https://img.shields.io/github/actions/workflow/status/yourusername/methodcache/ci.yml)](https://github.com/yourusername/methodcache/actions)
[![Coverage](https://img.shields.io/codecov/c/github/yourusername/methodcache)](https://codecov.io/gh/yourusername/methodcache)
[![License](https://img.shields.io/github/license/yourusername/methodcache)](LICENSE)

> **Unobtrusive, high‑performance method caching for .NET** – compile‑time decorators, runtime configuration, first‑class metrics, and production‑ready Redis/ETag integrations.

MethodCache lets you switch on caching with a single attribute, tune behaviour via a fluent API or configuration files, and even retrofit caching onto third‑party interfaces without touching their code. The Roslyn source generator produces zero‑reflection proxies, while the analyzers keep your usage safe and consistent.

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
await cacheConfig.UpdateAsync(
    "MethodCache:Services:MyApp.Services.IOrdersService:GetAsync:Duration",
    TimeSpan.FromMinutes(1).ToString());
```

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

### Performance & Resilience
- Compile‑time proxies, async‑first pipelines, stampede protection, distributed locks.
- Circuit breakers and retry policies integrated with Redis provider.
- Hybrid L1/L2 cache with optional compression and multi‑region write‑through.

### Observability & Operations
- Metrics hook (`ICacheMetrics`), logging callbacks, stats for in‑memory manager.
- Analyzer diagnostics (MC0001–MC0004) catch misconfigurations early.
- Runtime management APIs to enable/disable caches or retune durations live.

### Developer Experience
- Fluent API with `.WithVersion`, `.WithKeyGenerator<T>`, `.When(predicate)` for precise control.
- Analyzer + generator packages deliver IntelliSense, validation, and automatic registry generation.
- Built-in ETag middleware and decorator support for HTTP caching scenarios.

---

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
