# MethodCache Fluent API Specification

## Overview
This document defines the fluent API that will sit on top of MethodCache's existing caching infrastructure. The goal is to provide an intuitive, discoverable, and type-safe developer experience while retaining compatibility with the current attribute-based and configuration-centric workflows. The fluent API must compose cleanly with dependency injection (DI), work across MethodCache's L1/L2 layers, and unlock richer policy control without sacrificing performance.

## Goals
- Deliver a fluent, chainable API surface that is easy to discover via IntelliSense.
- Maintain strong typing across cache operations, configuration, invalidation, and observability.
- Preserve performance characteristics expected from MethodCache, including low allocation hot paths.
- Offer first-class support for multi-layer caching, tagging, ETags, distributed locking, and cache stampede prevention.
- Provide a configuration experience that aligns with modern .NET DI patterns.
- Enable straightforward unit testing, integration testing, and production observability.

## Non-Goals
- Replacing or deprecating attribute-based caching in the initial release.
- Shipping transport or provider changes beyond what is required to support the fluent API.
- Introducing breaking changes to the public surface of MethodCache.Core outside of the newly proposed namespaces.

## Design Principles
- **Fluent & Composable**: APIs should support readable chains while remaining explicit about behavior.
- **Discoverable**: Naming, overloads, and XML documentation will be curated to guide developers toward best practices.
- **Type-Safe**: Generics and typed builders reduce runtime casting and key mismatches.
- **Performance-Aware**: Favor `ValueTask`, pooled builders, struct-based results, and minimized allocations on hot paths.
- **Testable**: Avoid statics; rely on DI-friendly abstractions to ease mocking and unit testing.
- **Layer-Agnostic**: API semantics should not leak internal cache layer specifics but must allow configuration of L1/L2 providers.
- **Production-Ready**: Include primitives required for real-world deployments (locking, stampede protection, metrics, bulk operations).

## High-Level Architecture
The fluent API is delivered via extension methods on existing public abstractions to avoid large-scale refactoring:

1. **Core API** — `MethodCache.Core.Extensions` contains `ICacheManager` extensions for single/bulk get-create, try-get, streaming, and invalidation.
2. **ETag API** — `MethodCache.ETags.Extensions` extends `IETagCacheManager` with fluent helpers that align with controller usage patterns.
3. **Configuration API** — `MethodCache.Core.Configuration.Fluent` introduces fluent builders wired into `IServiceCollection.AddMethodCache`.
4. **Supporting Types** — New option builders (`CacheEntryOptions`), context objects (`CacheContext`), key builders, MVC helpers, metrics contracts, and streaming options.

The fluent API composes with existing middleware, providers, and analyzers. Source generators will be updated to rely on the shared fluent configuration primitives internally, maintaining a single source of truth for policy defaults.

## API Surface Details

### Method Chaining API (NEW!)
Namespace: `MethodCache.Core.Extensions` and `MethodCache.Core.Fluent`.

**Overview**: The Method Chaining API provides an intuitive, chainable interface for configuring cache operations. This API transforms the callback-based configuration into a more readable and discoverable fluent interface.

```csharp
public static class CacheManagerExtensions
{
    // Method Chaining API - Primary entry points
    public static CacheBuilder<T> Cache<T>(
        this ICacheManager cacheManager,
        Func<ValueTask<T>> factory,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default);

    public static CacheBuilder<T> Build<T>(
        this ICacheManager cacheManager,
        Func<ValueTask<T>> factory,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default);
}

public class CacheBuilder<T>
{
    // Duration and expiration
    public CacheBuilder<T> WithDuration(TimeSpan duration);
    public CacheBuilder<T> WithRefreshAhead(TimeSpan window);
    public CacheBuilder<T> WithSlidingExpiration(TimeSpan slidingExpiration);

    // Tags and versioning
    public CacheBuilder<T> WithTags(params string[] tags);
    public CacheBuilder<T> WithVersion(int version);

    // Key generation
    public CacheBuilder<T> WithKeyGenerator(ICacheKeyGenerator keyGenerator);
    public CacheBuilder<T> WithKeyGenerator<TKeyGenerator>() where TKeyGenerator : ICacheKeyGenerator, new();

    // Advanced features
    public CacheBuilder<T> WithStampedeProtection(StampedeProtectionMode mode = StampedeProtectionMode.Probabilistic, double beta = 1.0, TimeSpan? refreshAheadWindow = null);
    public CacheBuilder<T> WithDistributedLock(TimeSpan timeout, int maxConcurrency = 1);
    public CacheBuilder<T> WithMetrics(ICacheMetrics metrics);

    // Callbacks and conditions
    public CacheBuilder<T> OnHit(Action<CacheContext> onHit);
    public CacheBuilder<T> OnMiss(Action<CacheContext> onMiss);
    public CacheBuilder<T> When(Func<CacheContext, bool> predicate);

    // Configuration
    public CacheBuilder<T> WithServices(IServiceProvider services);
    public CacheBuilder<T> WithCancellationToken(CancellationToken cancellationToken);

    // Execution
    public ValueTask<T> ExecuteAsync();
}
```

**Usage Examples**:

```csharp
// Simple usage
var user = await cache.Cache(() => userService.GetUserAsync(userId))
    .WithDuration(TimeSpan.FromHours(1))
    .WithTags("user", $"user:{userId}")
    .ExecuteAsync();

// Advanced configuration
var orders = await cache.Cache(() => orderService.GetOrdersAsync(customerId, status))
    .WithDuration(TimeSpan.FromMinutes(30))
    .WithStampedeProtection()
    .WithKeyGenerator<JsonKeyGenerator>()
    .When(ctx => customerId > 0)
    .OnHit(ctx => logger.LogInformation($"Cache hit: {ctx.Key}"))
    .OnMiss(ctx => logger.LogInformation($"Cache miss: {ctx.Key}"))
    .ExecuteAsync();

// Multiple key generators based on conditions
var builder = cache.Cache(() => service.ProcessDataAsync(request))
    .WithDuration(TimeSpan.FromMinutes(30));

if (request.IsComplex)
    builder = builder.WithKeyGenerator<MessagePackKeyGenerator>();
else if (request.IsDebugMode)
    builder = builder.WithKeyGenerator<JsonKeyGenerator>();
else
    builder = builder.WithKeyGenerator<FastHashKeyGenerator>();

var result = await builder.ExecuteAsync();
```

**Benefits**:
- **Intuitive**: Natural reading flow with method chaining
- **Type-safe**: Generic key generator selection with compile-time validation
- **Discoverable**: IntelliSense guides through available options
- **Flexible**: Conditional configuration based on runtime values
- **Performance**: No overhead - compiles to same efficient code as callback-based API
- **Backward Compatible**: Existing APIs continue to work unchanged

### ICacheManager Extensions
Namespace: `MethodCache.Core.Extensions`.

```csharp
public static class CacheManagerExtensions
{
    public static ValueTask<T> GetOrCreateAsync<T>(
        this ICacheManager cacheManager,
        string key,
        Func<CacheContext, CancellationToken, ValueTask<T>> factory,
        Action<CacheEntryOptions.Builder>? configure = null,
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
        CancellationToken cancellationToken = default);

    public static IAsyncEnumerable<T> GetOrCreateStreamAsync<T>(
        this ICacheManager cacheManager,
        string key,
        Func<CacheContext, CancellationToken, IAsyncEnumerable<T>> factory,
        Action<StreamCacheOptions.Builder>? configure = null,
        CancellationToken cancellationToken = default);

    public static Task InvalidateByKeysAsync(
        this ICacheManager cacheManager,
        IEnumerable<string> keys,
        CancellationToken cancellationToken = default);

    public static Task InvalidateByTagsAsync(
        this ICacheManager cacheManager,
        IEnumerable<string> tags,
        CancellationToken cancellationToken = default);

    public static Task InvalidateByTagPatternAsync(
        this ICacheManager cacheManager,
        string pattern,
        CancellationToken cancellationToken = default);
}
```

- `CacheLookupResult<T>` is an allocation-free struct containing `bool Found` and `T? Value`. When `Found` is false, `Value` is default.
- `GetOrCreateManyAsync` batches cache lookups and invokes the factory with only missing keys, enabling single round trips to L2 providers.
- `GetOrCreateStreamAsync` supports caching of streaming data. Implementations may buffer segments or apply windowing based on `StreamCacheOptions`.
- Invalidation APIs accept `IEnumerable<string>` to support streaming scenarios; params overloads forward to the core method for convenience. `InvalidateByTagPatternAsync` enables wildcard invalidation in providers that support it (e.g., Redis key scanning).

### ETag Caching Extensions
Namespace: `MethodCache.ETags.Extensions`.

```csharp
public static class ETagCacheManagerExtensions
{
    public static ValueTask<ETagCacheResult<T>> GetOrCreateWithETagAsync<T>(
        this IETagCacheManager manager,
        string key,
        Func<CacheContext, CancellationToken, ValueTask<ETagCacheEntry<T>>> factory,
        string? ifNoneMatch = null,
        Action<ETagCacheEntryOptions.Builder>? configure = null,
        CancellationToken cancellationToken = default);
}
```

- `ETagCacheResult<T>` carries status (`NotModified`, `Hit`, `Miss`) and the value where applicable.
- `ETagCacheEntry<T>` is unchanged but builder hooks allow fluent configuration of ETag strategy, weak/strong semantics, and stampede protection alignment.

### Fluent Configuration API
Namespace: `MethodCache.Core.Configuration.Fluent`.

```csharp
public static class MethodCacheServiceCollectionExtensions
{
    public static IServiceCollection AddMethodCache(
        this IServiceCollection services,
        Action<IMethodCacheConfiguration> configure);
}

public interface IMethodCacheConfiguration
{
    IMethodCacheConfiguration DefaultPolicy(Action<CachePolicy.Builder> configure);
    IMethodCacheConfiguration AddPolicy(string name, Action<CachePolicy.Builder> configure);
    IServiceConfiguration<TService> ForService<TService>();
    IGroupConfiguration ForGroup(string name);
}

public interface IServiceConfiguration<TService>
{
    IMethodConfiguration<TService> Method(Expression<Func<TService, Task>> method);
    IMethodConfiguration<TService, TResult> Method<TResult>(Expression<Func<TService, Task<TResult>>> method);
    IMethodConfiguration<TService> Method(Expression<Action<TService>> method);
}

public interface IMethodConfiguration<TService>
{
    IMethodConfiguration<TService> WithPolicy(string name);
    IMethodConfiguration<TService> Configure(Action<CacheEntryOptions.Builder> configure);
    IMethodConfiguration<TService> When(Func<CacheContext, bool> predicate);
    IMethodConfiguration<TService> WithDistributedLock(TimeSpan? timeout = null, int maxConcurrency = 1);
}

public interface IMethodConfiguration<TService, TResult> : IMethodConfiguration<TService>
{
    IMethodConfiguration<TService, TResult> TransformResult(Func<TResult, TResult> transform);
}

public interface IGroupConfiguration
{
    IGroupConfiguration Configure(Action<CacheEntryOptions.Builder> configure);
    IGroupConfiguration WithPolicy(string name);
    IGroupConfiguration WithDistributedLock(TimeSpan? timeout = null, int maxConcurrency = 1);
}
```

- Distributed locking metadata is captured at configuration time and mapped to underlying providers supporting lock semantics (e.g., Redis RedLock).
- Async overloads accept `Task`/`Task<TResult>` expressions, enabling configuration of both synchronous and asynchronous methods.
- Builders fan out to the existing options infrastructure, centralizing validation and defaults.
- Group configuration targets cross-cutting concerns (e.g., `"catalog"` group) and can apply distributed lock policies.

### Supporting Types

#### CacheEntryOptions
Namespace: `MethodCache.Core.Options`.

```csharp
public sealed class CacheEntryOptions
{
    public TimeSpan? Duration { get; }
    public IReadOnlyList<string> Tags { get; }
    public int? Version { get; }
    public Type? KeyGeneratorType { get; }
    public Func<CacheContext, bool>? Predicate { get; }
    public IReadOnlyList<Action<CacheContext>> OnHitCallbacks { get; }
    public IReadOnlyList<Action<CacheContext>> OnMissCallbacks { get; }
    public TimeSpan? RefreshAhead { get; }
    public StampedeProtectionOptions? StampedeProtection { get; }
    public DistributedLockOptions? DistributedLock { get; }
    public IReadOnlyList<string> DependentKeys { get; }
    public IReadOnlyList<string> CascadeKeys { get; }
    public ICacheMetrics? Metrics { get; }
    public CacheFallbackPolicy? FallbackPolicy { get; }

    public sealed class Builder
    {
        public Builder WithDuration(TimeSpan duration);
        public Builder WithTags(params string[] tags);
        public Builder WithVersion(int version);
        public Builder WithKeyGenerator<TGenerator>() where TGenerator : ICacheKeyGenerator;
        public Builder When(Func<CacheContext, bool> predicate);
        public Builder OnHit(Action<CacheContext> onHit);
        public Builder OnMiss(Action<CacheContext> onMiss);
        public Builder RefreshAhead(TimeSpan leadDuration);
        public Builder WithStampedeProtection(StampedeProtectionMode mode = StampedeProtectionMode.Probabilistic, double beta = 1.0);
        public Builder WithMetrics(ICacheMetrics metrics);
        public Builder WithFallback<TException>(Func<TException, CacheContext, ValueTask<object?>> fallback) where TException : Exception;
        public Builder WithCircuitBreaker(int handledEventsAllowedBeforeBreaking = 3, TimeSpan? durationOfBreak = null);
        public Builder DependsOn(params string[] parentKeys);
        public Builder InvalidatesWith(params string[] relatedKeys);
        public Builder CascadeInvalidation(bool enabled = true);
        public CacheEntryOptions Build();
    }
}
```

- `CacheEntryOptions` instances are immutable. Builders are pooled via `ObjectPool<CacheEntryOptions.Builder>` and reset between uses.
- `WithFallback` captures exception-specific fallback behavior. Internally, builder stores typed policies that are materialized per entry.
- Dependency methods support cascading invalidation across related keys. `CascadeInvalidation(true)` turns on automatic invalidation of dependent cache entries when the root is invalidated.

#### Supporting Option Types

```csharp
public sealed record StampedeProtectionOptions(StampedeProtectionMode Mode, double Beta, TimeSpan? RefreshAheadWindow);
public enum StampedeProtectionMode { None, DistributedLock, Probabilistic, RefreshAhead }

public sealed record DistributedLockOptions(TimeSpan Timeout, int MaxConcurrency);

public sealed record CacheFallbackPolicy(
    IReadOnlyDictionary<Type, Func<Exception, CacheContext, ValueTask<object?>>> Handlers,
    CircuitBreakerOptions? CircuitBreaker);

public sealed record CircuitBreakerOptions(int HandledEventsAllowedBeforeBreaking, TimeSpan DurationOfBreak);
```

- `StampedeProtectionOptions` unifies probabilistic early refresh, distributed locking, and refresh-ahead strategies.
- `DistributedLockOptions` is interpreted by providers; when `Timeout` is null, providers fallback to default lock timeout.

#### CacheContext
Namespace: `MethodCache.Core.Runtime`.

```csharp
public sealed class CacheContext
{
    public IServiceProvider Services { get; }
    public IDictionary<object, object?> Items { get; }
    public string Key { get; }
    public CacheLayer Layer { get; }
    public CancellationToken CancellationToken { get; }
    public IReadOnlyDictionary<string, object?> Scope { get; }

    public DistributedLockHandle? DistributedLock { get; }
}
```

- `Scope` exposes enriched metadata (tenant, user, locale) supplied by hosting integrations. Convenience extensions (e.g., `CacheContextExtensions.GetTenantId`) provide typed accessors without polluting the core type.
- `Items` is scoped per call to enable user code to pass data between callbacks without leaking state.
- `DistributedLock` exposes context about acquired locks for advanced scenarios and diagnostics.

#### CacheContext Extensions

```csharp
public static class CacheContextExtensions
{
    public static string? GetTenantId(this CacheContext context) =>
        context.Scope.TryGetValue("TenantId", out var value) ? value?.ToString() : null;
}
```

- Additional helpers (e.g., `GetUserId`, `GetCulture`) follow the same pattern to preserve flexibility.

#### CacheKeyBuilder
Namespace: `MethodCache.Core.Keys`.

```csharp
public sealed class CacheKeyBuilder
{
    public CacheKeyBuilder(string prefix);
    public CacheKeyBuilder Append(string name, ReadOnlySpan<char> value);
    public CacheKeyBuilder Append<T>(string name, T value, string? format = null, IFormatProvider? provider = null);
    public string Build();
}
```

- `Append` enforces invariant casing and null-safe formatting.
- `Build` returns pooled `string` instances via `StringBuilderCache` to reduce allocations.

#### StreamCacheOptions
Namespace: `MethodCache.Core.Options`.

```csharp
public sealed class StreamCacheOptions
{
    public TimeSpan? Duration { get; }
    public int SegmentSize { get; }
    public bool EnableWindowing { get; }

    public sealed class Builder
    {
        public Builder WithDuration(TimeSpan duration);
        public Builder WithSegmentSize(int segmentSize);
        public Builder EnableWindowing(bool enabled = true);
        public StreamCacheOptions Build();
    }
}
```

- Streaming options allow providers to tune buffering strategies for `IAsyncEnumerable` payloads.

#### Controller Helper
Namespace: `MethodCache.ETags.Mvc`.

```csharp
public static class MethodCacheControllerExtensions
{
    public static IActionResult From<T>(this ControllerBase controller, ETagCacheResult<T> result, Func<T, IActionResult>? onHit = null);
}
```

- Defaults map `NotModified` to `StatusCodeResult(304)` and `Miss`/`Hit` to `OkObjectResult` unless `onHit` overrides serialization.
- Result helpers live in a dedicated MVC package (`MethodCache.ETags.Mvc`) to avoid hard dependencies from core libraries to ASP.NET Core.

#### Observability Interfaces
Namespace: `MethodCache.Core.Metrics`.

```csharp
public interface ICacheMetrics
{
    void RecordHit(string key, TimeSpan duration, CacheLayer layer);
    void RecordMiss(string key, TimeSpan duration);
    void RecordEviction(string key, EvictionReason reason);
}
```

- Providers opt-in to metrics by wiring `ICacheMetrics` implementations via DI. `WithMetrics` attaches metrics observers on a per-entry basis.

## Usage Examples

### Basic Caching
```csharp
var user = await cacheManager.GetOrCreateAsync(
    key: CacheKeys.Users.ById(userId),
    factory: static async (context, ct) => await repository.GetUserAsync(userId, ct),
    configure: options => options.WithDuration(TimeSpan.FromHours(1))
);
```

### Conditional Caching with Tags and Stampede Protection
```csharp
var products = await cacheManager.GetOrCreateAsync(
    key: CacheKeys.Products.All,
    factory: static (context, ct) => repository.GetProductsAsync(ct),
    configure: options => options
        .WithDuration(TimeSpan.FromMinutes(30))
        .WithTags("products", "catalog")
        .When(ctx => ctx.Scope.TryGetValue("IsPremium", out var flag) && flag is true)
        .WithStampedeProtection(StampedeProtectionMode.Probabilistic, beta: 1.25)
);
```

### Bulk Get with Single Round Trip
```csharp
var users = await cacheManager.GetOrCreateManyAsync(
    keys: userIds.Select(CacheKeys.Users.ById),
    factory: static async (missing, context, ct) =>
    {
        var results = await repository.GetUsersByIdsAsync(missing, ct);
        return results.ToDictionary(user => CacheKeys.Users.ById(user.Id), user => user);
    },
    configure: options => options.WithDuration(TimeSpan.FromHours(1))
);
```

### Streaming Cache
```csharp
await foreach (var dto in cacheManager.GetOrCreateStreamAsync(
    key: CacheKeys.Reports.ByMonth(year, month),
    factory: static (context, ct) => reportService.GetMonthlyReportStreamAsync(year, month, ct),
    configure: options => options
        .WithDuration(TimeSpan.FromMinutes(10))
        .WithSegmentSize(500)
        .EnableWindowing()
))
{
    yield return dto;
}
```

### Distributed Lock in Configuration
```csharp
services.AddMethodCache(config =>
{
    config.DefaultPolicy(policy => policy
        .Duration(TimeSpan.FromMinutes(15))
        .Strategy(CacheStrategy.Hybrid)
        .RefreshAhead(TimeSpan.FromMinutes(5))
        .WithStampedeProtection(StampedeProtectionMode.DistributedLock)
    );

    config.ForService<IUserService>()
          .Method(s => s.GetUserAsync(default))
          .WithPolicy("long-term")
          .WithDistributedLock(TimeSpan.FromSeconds(5), maxConcurrency: 2)
          .Configure(options => options
              .WithTags("users")
              .WithMetrics(metrics)
              .DependsOn("tenant:" + tenantId)
          );
});
```

### Unit Testing
```csharp
var cacheManager = Substitute.For<ICacheManager>();
cacheManager.GetOrCreateAsync(
    Arg.Any<string>(),
    Arg.Any<Func<CacheContext, CancellationToken, ValueTask<User>>>(),
    Arg.Any<Action<CacheEntryOptions.Builder>>(),
    Arg.Any<CancellationToken>())
.Returns(new ValueTask<User>(user));
```

## Implementation Strategy
1. **Dual Support**: Implement both attribute-based and fluent APIs. Internally, attributes build fluent configuration objects to reduce code duplication.
2. **Source Generator Integration**: Update the generator to emit fluent configuration calls for decorated methods while maintaining clean public APIs.
3. **Documentation**: Provide comprehensive guides showing both attribute and fluent approaches with best practices for distributed locking and stampede protection.

## Validation & Testing
- Add xUnit coverage for each fluent builder path, ensuring configuration maps to existing runtime behaviors.
- Extend integration tests to cover new invalidation overloads, bulk retrieval semantics, streaming scenarios, and stampede protection.
- Update analyzers to flag unsupported method signatures or invalid fluent configurations early (e.g., distributed lock without provider support).
- Add performance benchmarks for `TryGetAsync`, `GetOrCreateManyAsync`, and probabilistic stampede prevention paths.

## Implementation Roadmap
1. **Phase 1 – Core Foundations**: Implement supporting types (`CacheEntryOptions`, `CacheLookupResult`, `CacheKeyBuilder`, `CacheContext` enrichment) and single-entry APIs.
2. **Phase 2 – Resilience & Concurrency**: Introduce distributed locking primitives, stampede protection, and circuit breaker options.
3. **Phase 3 – Bulk & Metrics**: Deliver `GetOrCreateManyAsync`, metrics hooks, and wildcard invalidation across providers.
4. **Phase 4 – Dependencies & Cascading**: Implement dependency tracking, cascading invalidation, and analyzer support for dependency cycles.
5. **Phase 5 – Streaming & Advanced Scenarios**: Add streaming cache support, controller helpers, and finalize documentation & samples.

## Resolved Questions
- **Scoped Metadata Access**: Provide extension methods (e.g., `CacheContextExtensions.GetTenantId`) rather than new properties to keep `CacheContext` lean.
- **Sync Overloads**: Async-first API remains default. Optional sync wrappers may be delivered in a separate legacy compatibility package.
- **Metrics Hooks**: Metrics integration is centralized via `ICacheMetrics` and `WithMetrics`, avoiding ad-hoc hooks in high-level APIs.
- **Tag Wildcards**: `InvalidateByTagPatternAsync` provides pattern-based invalidation aligned with provider capabilities.

## Additional Recommendations

### OpenTelemetry Integration
Extend `ICacheMetrics` with dimension/tag support for richer observability:

```csharp
public interface ICacheMetrics
{
    void RecordHit(string key, TimeSpan duration, CacheLayer layer, IReadOnlyDictionary<string, object?>? tags = null);
    void RecordMiss(string key, TimeSpan duration, IReadOnlyDictionary<string, object?>? tags = null);
    void RecordEviction(string key, EvictionReason reason, IReadOnlyDictionary<string, object?>? tags = null);
}
```

This enables rich telemetry with tenant IDs, service names, and custom dimensions for monitoring dashboards.

### Streaming Results for Large Bulk Operations
Consider adding streaming overload for `GetOrCreateManyAsync` to handle very large result sets:

```csharp
public static IAsyncEnumerable<(string Key, T Value)> GetOrCreateManyStreamAsync<T>(
    this ICacheManager cacheManager,
    IAsyncEnumerable<string> keys,
    Func<IReadOnlyList<string>, CacheContext, CancellationToken, ValueTask<IDictionary<string, T>>> factory,
    Action<CacheEntryOptions.Builder>? configure = null,
    CancellationToken cancellationToken = default);
```

### Tenant-Aware Cascading Invalidation
Add prefix-based dependency graphs for multi-tenant scenarios:

```csharp
public Builder DependsOnPrefix(string keyPrefix);
public Builder InvalidatesPrefix(string keyPrefix);
```

### Compile-Time Configuration Validation
Add analyzer rules to validate distributed lock configurations:
- Warn when `WithDistributedLock` is used without a distributed provider
- Validate timeout values are reasonable
- Check for circular dependency graphs

## Open Questions
- How should stampede protection interact with streaming results (buffering vs. partial invalidation)?
- Should dependency tracking support regex patterns for complex invalidation scenarios?

## Implementation Plan Checklist
- [ ] Implement supporting types and builder pooling.
- [ ] Extend `ICacheManager`/`IETagCacheManager` with new extension methods and add unit tests.
- [ ] Update configuration builders, including distributed lock and dependency metadata.
- [ ] Wire metric recording and stampede protection into Hybrid cache orchestration.
- [ ] Update analyzers and source generator logic to emit fluent API calls.
- [ ] Refresh documentation, samples, and add benchmarking harnesses.
- [ ] Gather beta feedback from sample apps and integration tests; iterate before general release.

## Implementation Status & Next Steps
- ✅ Fluent runtime entry points (`CacheManagerExtensions.GetOrCreateAsync` / `TryGetAsync` / `GetOrCreateManyAsync`) plus supporting `CacheEntryOptions`, `CacheLookupResult`, and `CacheContext` types now wrap the existing `ICacheManager` without altering its contract, including sliding expiration, refresh-ahead scheduling, and hit/miss callbacks.
- ✅ Fluent configuration adapter (`configuration.ApplyFluent`, `AddMethodCacheFluent`) maps the new builders onto `CacheMethodSettings`, with tests covering default policies, per-method overrides, group inheritance, and DI integration.
- ✅ Source generator now emits fluent configuration (`config.ApplyFluent`) and carries attribute metadata (duration, tags, group, idempotency) into the runtime pipeline; corresponding unit tests assert the new output.
- 🔄 Upcoming work focuses on enriching analyzers/decorators with advanced options (distributed locks, stampede protection, metrics) and layering streaming helpers.
- ⏳ Streaming APIs, distributed locking, stampede protection, per-entry metrics, and dependency/cascade invalidation remain queued for future phases.
