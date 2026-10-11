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

1. `ICacheManager` extensions for get/create flows.
2. Builder APIs used during `AddMethodCache` setup.

Both are in the `MethodCache` namespace.
3. Supporting types: option builders and contexts.

The configuration builders feed the same policy pipeline as attributes, appsettings.json and runtime overrides.

## API Surface Details

### ICacheManager Extensions
Namespace: `MethodCache`.

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
        int? version = null,
        CancellationToken cancellationToken = default);

    public static ValueTask<IDictionary<string, T>> GetOrCreateManyAsync<T>(
        this ICacheManager cacheManager,
        IEnumerable<string> keys,
        Func<IReadOnlyList<string>, CacheContext, CancellationToken, ValueTask<IDictionary<string, T>>> factory,
        Action<CacheEntryOptions.Builder>? configure = null,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default);
}
```

- `CacheLookupResult<T>` is an allocation-free struct containing `bool Found` and `T? Value`. When `Found` is false, `Value` is default.
- `GetOrCreateManyAsync` batches cache lookups and invokes the factory with only missing keys, enabling single round trips to L2 providers.
- Concurrent `GetOrCreateAsync` calls for the same key share one factory run. A caller's `cancellationToken` stops its own wait. If it cancels a factory run that other callers joined, they run the factory again rather than fail, and fail-safe never treats a caller's own cancellation as a failing dependency.
- An entry stored with `WithVersion(n)` is found by `TryGetAsync` with the same `version`, and by `GetOrCreateAsync` and `GetOrCreateManyAsync` configured with `WithVersion(n)`.
- To cache a sequence, materialize it in the factory (for example `await stream.ToListAsync(ct)`) and cache the list.
- Invalidation lives on `ICacheManager` itself: `InvalidateByKeysAsync(params string[] keys)`, `InvalidateByTagsAsync(params string[] tags)` and `InvalidateByTagPatternAsync(string pattern)`.
- `InvalidateByTagPatternAsync` wildcard syntax is `*` (zero or more chars) and `?` (exactly one char). Other characters are literal, and matching is against the full tag string.
- Distributed cache caveat: pattern invalidation works on the in-memory cache only. With a distributed cache configured it throws `NotSupportedException`, because the distributed cache cannot list its tags.

### Fluent Configuration API
Namespace: `MethodCache`. Use it inside `AddMethodCache` to configure `[Cache]` methods without changing the attributes.

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
    IFluentMethodConfiguration<TResult> Method<TResult>(Expression<Func<TService, TResult>> method);
    IFluentMethodConfiguration Method(Expression<Func<TService, Task>> method);
    IFluentMethodConfiguration<TResult> Method<TResult>(Expression<Func<TService, Task<TResult>>> method);
    IFluentMethodConfiguration<TResult> Method<TResult>(Expression<Func<TService, ValueTask<TResult>>> method);
}

public interface IFluentMethodConfiguration
{
    IFluentMethodConfiguration Configure(Action<CacheEntryOptions.Builder> configure);
    IFluentMethodConfiguration WithGroup(string groupName);
    IFluentMethodConfiguration WithVersion(int version);
}

// Returned when the method has a result; TResult is the awaited type for async methods.
public interface IFluentMethodConfiguration<TResult> : IFluentMethodConfiguration
{
    IFluentMethodConfiguration<TResult> CacheWhen(Func<TResult, bool> predicate);
    // Configure, WithGroup and WithVersion return IFluentMethodConfiguration<TResult> here.
}

public interface IFluentGroupConfiguration
{
    IFluentGroupConfiguration Configure(Action<CacheEntryOptions.Builder> configure);
}
```

- Each setting comes from the most specific place that sets it: the method itself (its `[Cache]` attribute or `ForService<T>().Method(...)`), then its group, then `DefaultPolicy`. `DefaultPolicy` applies to every cached method, also ones that only have `[Cache]`. A method joins a group with `[Cache("name")]` or `WithGroup("name")`.
- `CacheWhen` stores a result only when the predicate returns true. A rejected result is still returned to the caller, the entry it would have replaced (including a fail-safe value) stays, and the next call runs the method again. The predicate runs once per load, never on a cache hit; if it throws, the result is returned and not stored. It is set from code only, so it can't come from appsettings.
- Cached methods take duration, tags, sliding expiration, refresh-ahead and version from here. `OnHit`, `OnMiss`, `WithMetrics` and `When` only apply to `GetOrCreateAsync`; configuring them for a cached method throws `InvalidOperationException` at startup instead of being ignored.

### Supporting Types

#### CacheEntryOptions
Namespace: `MethodCache`.

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
Namespace: `MethodCache`.

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

#### Observability Interfaces
Namespace: `MethodCache.Extensibility`.

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

    // Don't remember empty search results: the next search hits the database again.
    config.ForService<IProductService>()
          .Method(s => s.SearchAsync(Any<string>.Value))
          .CacheWhen(results => results.Count > 0);
});
```

### Unit Testing
`GetOrCreateAsync` is an extension method, so mock libraries cannot intercept it. Pass a real `ICacheManager` instead:

```csharp
ICacheManager cacheManager = new NoOpCacheManager(); // never caches: the factory runs on every call
var service = new OrderService(repository, cacheManager);
```

`NoOpCacheManager` is in `MethodCache.Extensibility`.
