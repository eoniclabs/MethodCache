# MethodCache Fluent API Guide

## Overview

This guide explains how to use MethodCache's fluent API for explicit cache operations.

Use the fluent API when:

- You do not want attribute-based caching on service contracts.
- You are caching third-party libraries or SDK clients.
- You need per-call policy control (duration, tags, conditions).

## Quick Start

```csharp
var order = await cache.GetOrCreateAsync(
    $"order:{id}",
    async (ctx, ct) => await repository.GetByIdAsync(id),
    o => o.WithDuration(TimeSpan.FromMinutes(30))
          .WithTags("orders", $"order:{id}"));
```

You always pass the cache key yourself. MethodCache does not derive keys from lambdas, so captured variables never end up in a key by accident.

## What the Fluent API Provides

- Explicit-key `GetOrCreateAsync`, `TryGetAsync`, bulk and streaming helpers on top of `ICacheManager`.
- Type-safe configuration for expiration (absolute, sliding, refresh-ahead), tags, and versioning.
- The same in-memory cache, distributed cache and invalidation as `[Cache]` methods. Policies passed here live in code; configuration files and runtime overrides do not change them.

## High-Level Architecture

The fluent API is exposed through extension methods and builders:

1. `MethodCache.Core.Infrastructure.Extensions`: `ICacheManager` extensions for get/create flows.
2. `MethodCache.Core.Configuration.Surfaces.Fluent`: builder APIs used during `AddMethodCache` setup.
3. Supporting types: option builders and contexts.

The configuration builders feed the same policy pipeline as attributes, appsettings.json and runtime overrides.

## API Surface Details

### ICacheManager Extensions
Namespace: `MethodCache.Core.Infrastructure.Extensions`.

```csharp
public static class CacheManagerExtensions
{
    public static ValueTask<T> GetOrCreateAsync<T>(
        this ICacheManager cacheManager,
        string key,
        Func<CacheContext, CancellationToken, ValueTask<T>> factory,
        Action<CacheEntryOptions.Builder>? configure = null,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default);

    public static ValueTask<CacheLookupResult<T>> TryGetAsync<T>(
        this ICacheManager cacheManager,
        string key,
        CancellationToken cancellationToken = default);

    public static ValueTask<IDictionary<string, T>> GetOrCreateManyAsync<T>(
        this ICacheManager cacheManager,
        IEnumerable<string> keys,
        Func<IReadOnlyList<string>, CacheContext, CancellationToken, ValueTask<IDictionary<string, T>>> factory,
        Action<CacheEntryOptions.Builder>? configure = null,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default);

    public static IAsyncEnumerable<T> GetOrCreateStreamAsync<T>(
        this ICacheManager cacheManager,
        string key,
        Func<CacheContext, CancellationToken, IAsyncEnumerable<T>> factory,
        Action<StreamCacheOptions.Builder>? configure = null,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default);

}
```

- `CacheLookupResult<T>` is an allocation-free struct containing `bool Found` and `T? Value`. When `Found` is false, `Value` is default.
- `GetOrCreateManyAsync` batches cache lookups and invokes the factory with only missing keys, enabling single round trips to L2 providers.
- `GetOrCreateStreamAsync` supports caching of streaming data. Implementations may buffer segments or apply windowing based on `StreamCacheOptions`.
- Invalidation lives on `ICacheManager` itself: `InvalidateByKeysAsync(params string[] keys)`, `InvalidateByTagsAsync(params string[] tags)` and `InvalidateByTagPatternAsync(string pattern)`.
- `InvalidateByTagPatternAsync` wildcard syntax is `*` (zero or more chars) and `?` (exactly one char). Other characters are literal, and matching is against the full tag string.
- Distributed cache caveat: pattern invalidation works on the in-memory cache only. With a distributed cache configured it throws `NotSupportedException`, because the distributed cache cannot list its tags.

### Fluent Configuration API
Namespace: `MethodCache.Core.Configuration.Surfaces.Fluent`. Use it inside `AddMethodCache` to configure `[Cache]` methods without changing the attributes.

```csharp
public interface IFluentMethodCacheConfiguration
{
    IFluentMethodCacheConfiguration DefaultPolicy(Action<CacheEntryOptions.Builder> configure);
    IFluentServiceConfiguration<TService> ForService<TService>();
    IFluentGroupConfiguration ForGroup(string name);
}

public interface IFluentServiceConfiguration<TService>
{
    IFluentMethodConfiguration Method(Expression<Action<TService>> method);
    IFluentMethodConfiguration Method<TResult>(Expression<Func<TService, TResult>> method);
    IFluentMethodConfiguration Method(Expression<Func<TService, Task>> method);
    IFluentMethodConfiguration Method<TResult>(Expression<Func<TService, Task<TResult>>> method);
}

public interface IFluentMethodConfiguration
{
    IFluentMethodConfiguration Configure(Action<CacheEntryOptions.Builder> configure);
    IFluentMethodConfiguration WithGroup(string groupName);
    IFluentMethodConfiguration WithVersion(int version);
}

public interface IFluentGroupConfiguration
{
    IFluentGroupConfiguration Configure(Action<CacheEntryOptions.Builder> configure);
}
```

- Settings are applied in order: the default policy, then the method's group (set with `WithGroup`), then the method itself. `DefaultPolicy` and `ForGroup` only apply to methods listed with `ForService<T>().Method(...)`.
- Cached methods take duration, tags, sliding expiration, refresh-ahead and version from here. `OnHit`, `OnMiss`, `WithMetrics` and `When` only apply to `GetOrCreateAsync`; configuring them for a cached method throws `InvalidOperationException` at startup instead of being ignored.

### Supporting Types

#### CacheEntryOptions
Namespace: `MethodCache.Core.Options`.

```csharp
public sealed class CacheEntryOptions
{
    public TimeSpan? Duration { get; }
    public TimeSpan? SlidingExpiration { get; }
    public TimeSpan? RefreshAhead { get; }
    public IReadOnlyList<string> Tags { get; }
    public IReadOnlyList<Action<CacheContext>> OnHitCallbacks { get; }
    public IReadOnlyList<Action<CacheContext>> OnMissCallbacks { get; }
    public ICacheMetrics? Metrics { get; }
    public int? Version { get; }
    public Func<CacheContext, bool>? Predicate { get; }

    public sealed class Builder
    {
        public Builder WithDuration(TimeSpan duration);
        public Builder WithTags(params string[] tags);
        public Builder WithSlidingExpiration(TimeSpan slidingExpiration);
        public Builder RefreshAhead(TimeSpan window);
        public Builder OnHit(Action<CacheContext> callback);
        public Builder OnMiss(Action<CacheContext> callback);
        public Builder WithMetrics(ICacheMetrics metrics);
        public Builder WithVersion(int version);
        public Builder When(Func<CacheContext, bool> predicate);
        public CacheEntryOptions Build();
    }
}
```

- `WithSlidingExpiration` extends an entry's lifetime each time it is read, but never past `Duration`.
- `RefreshAhead` reloads an entry in the background when a read happens within the window before it expires, so callers keep getting the cached value instead of waiting for the factory.
- `When` skips the cache for a call when the predicate returns false.

#### CacheContext
Namespace: `MethodCache.Core.Runtime.Core`.

```csharp
public sealed class CacheContext
{
    public string Key { get; }
    public IServiceProvider? Services { get; }
    public IDictionary<object, object?> Items { get; }
}
```

- `Key` is the key you passed to `GetOrCreateAsync`.
- `Services` is the service provider you passed in, if any.
- `Items` is scoped per call, so callbacks such as `OnHit`, `OnMiss` and `When` can share data without leaking state.

#### StreamCacheOptions
Namespace: `MethodCache.Core.Options`.

```csharp
public sealed class StreamCacheOptions
{
    public TimeSpan? Duration { get; }
    public int SegmentSize { get; }
    public int MaxMemorySize { get; }
    public bool EnableWindowing { get; }

    public sealed class Builder
    {
        public Builder WithDuration(TimeSpan duration);
        public Builder WithSegmentSize(int segmentSize);
        public Builder WithMaxMemorySize(int maxMemorySize);
        public Builder EnableWindowing(bool enabled = true);
        public StreamCacheOptions Build();
    }
}
```

- Streaming options allow providers to tune buffering strategies for `IAsyncEnumerable` payloads.

#### Observability Interfaces
Namespace: `MethodCache.Core.Infrastructure.Metrics`.

```csharp
public interface ICacheMetrics
{
    void RecordHit(string key, TimeSpan? duration, object? value);
    void RecordMiss(string key);
    void RecordError(string key, Exception exception);
}
```

- `WithMetrics` attaches an `ICacheMetrics` observer to a single `GetOrCreateAsync` call.

## Usage Examples

### Basic Caching
```csharp
var user = await cacheManager.GetOrCreateAsync(
    key: $"user:{userId}",
    factory: async (context, ct) => await repository.GetUserAsync(userId, ct),
    configure: options => options.WithDuration(TimeSpan.FromHours(1))
);
```

### Conditional Caching with Tags and Refresh-Ahead
```csharp
var products = await cacheManager.GetOrCreateAsync(
    key: "products:all",
    factory: async (context, ct) => await repository.GetProductsAsync(ct),
    configure: options => options
        .WithDuration(TimeSpan.FromMinutes(30))
        .RefreshAhead(TimeSpan.FromMinutes(5))
        .WithTags("products", "catalog")
        .When(ctx => includePremium)
);
```

### Bulk Get with Single Round Trip
```csharp
var users = await cacheManager.GetOrCreateManyAsync(
    keys: userIds.Select(id => $"user:{id}"),
    factory: async (missing, context, ct) =>
    {
        var results = await repository.GetUsersByIdsAsync(missing, ct);
        return results.ToDictionary(user => $"user:{user.Id}", user => user);
    },
    configure: options => options.WithDuration(TimeSpan.FromHours(1))
);
```

### Streaming Cache
```csharp
await foreach (var dto in cacheManager.GetOrCreateStreamAsync(
    key: $"report:{year}-{month:00}",
    factory: (context, ct) => reportService.GetMonthlyReportStreamAsync(year, month, ct),
    configure: options => options
        .WithDuration(TimeSpan.FromMinutes(10))
        .WithSegmentSize(500)
        .EnableWindowing()
))
{
    yield return dto;
}
```

### Configuring Cached Methods
```csharp
services.AddMethodCache(config =>
{
    config.DefaultPolicy(options => options
        .WithDuration(TimeSpan.FromMinutes(15))
        .RefreshAhead(TimeSpan.FromMinutes(5)));

    config.ForService<IUserService>()
          .Method(s => s.GetUserAsync(default))
          .Configure(options => options
              .WithSlidingExpiration(TimeSpan.FromMinutes(5))
              .WithTags("users"));
});
```

### Unit Testing
`GetOrCreateAsync` is an extension method, so mock libraries cannot intercept it. Pass a real `ICacheManager` instead:

```csharp
ICacheManager cacheManager = new NoOpCacheManager(); // never caches: the factory runs on every call
var service = new OrderService(repository, cacheManager);
```

`NoOpCacheManager` is in `MethodCache.Core.Runtime.Execution`.
