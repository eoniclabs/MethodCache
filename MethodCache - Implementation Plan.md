# MethodCache - Implementation Plan

## Overall Goal
Implement all remaining features for `MethodCache.Core` and `MethodCache.SourceGenerator` as detailed in the "MethodCache - Software Design Document.md".

## Phase 1: Core Caching Logic & Configuration Enhancements

**Objective**: Establish the foundational caching mechanism and complete the flexible runtime configuration system.

### MethodCache.Core Tasks:
1.  **Enhance `CacheMethodSettings`**:
    *   Add properties for `KeyGeneratorType`, `Condition` (Func<CacheExecutionContext, bool>), `OnHitAction` (Action<CacheExecutionContext>), `OnMissAction` (Action<CacheExecutionContext>).
2.  **Refine `IMethodConfiguration` and `MethodConfiguration`**:
    *   Implement `Duration(Func<CacheExecutionContext, TimeSpan>)`.
    *   Implement `TagWith(Func<CacheExecutionContext, string>)`.
    *   Implement `KeyGenerator<TGenerator>()`.
    *   Implement `When(Func<CacheExecutionContext, bool>)`.
    *   Implement `OnHit(Action<CacheExecutionContext>)`.
    *   Implement `OnMiss(Action<CacheExecutionContext>)`.
3.  **Implement Group-based Configuration**:
    *   Add `ForGroup(string groupName)` to `IMethodCacheConfiguration`.
    *   Create `IGroupConfiguration` interface and `GroupConfiguration` class.
    *   Ensure group settings are applied with correct precedence.
4.  **Implement Key Generators**:
    *   Create `MessagePackKeyGenerator` (default, using MessagePack for serialization).
    *   Create `FastHashKeyGenerator`.
    *   Integrate `ICacheKeyProvider` into key generation logic for all generators.
    *   Ensure `CacheMethodSettings.Version` is incorporated into the generated cache key.
5.  **Update `ICacheManager`**:
    *   Modify `GetOrCreateAsync` to accept `ICacheKeyGenerator` and `CacheMethodSettings` to dynamically generate keys and apply settings.

### MethodCache.SourceGenerator Tasks:
1.  **Generate Basic Caching Interception**:
    *   For methods marked with `[Cache]`, generate a basic decorator/proxy class.
    *   The generated code should:
        *   Call `ICacheManager.GetOrCreateAsync`.
        *   Pass a `CacheExecutionContext` instance to `GetOrCreateAsync`.
        *   Use the configured `ICacheKeyGenerator` (or default) to generate the key.
        *   Apply `CacheMethodSettings` (duration, tags, version, condition, event hooks) during the `GetOrCreateAsync` call.

## Phase 2: Advanced Features & Resilience

**Objective**: Introduce more sophisticated caching behaviors and initial resilience patterns.

### MethodCache.Core Tasks:
1.  **Dynamic Configuration Updates**:
    *   Implement `ICacheConfigurationService` with methods for `UpdateMethodConfigurationAsync`, `EnableCachingAsync`, `UpdateGlobalSettingsAsync`, `InvalidateConfigurationCacheAsync`.
    *   Develop an internal mechanism for runtime configuration updates (e.g., using `IOptionsMonitor` or similar pattern).
2.  **Testing Support**:
    *   Implement `NoOpCacheProvider` (disables caching).
    *   Implement `MockCacheProvider` (configurable hit/miss behavior).
3.  **Concurrency & Idempotency**:
    *   Implement cache stampede prevention using `ConcurrentDictionary<string, Lazy<Task>>` or similar.
    *   Enforce `RequireIdempotent` attribute (e.g., by throwing an exception if not marked and caching is attempted).
4.  **Initial Resilience**:
    *   Implement a basic circuit breaker mechanism (e.g., using Polly) for cache operations (read/write).
    *   Ensure cache failures are logged and treated as cache misses (fail-safe logic).

### MethodCache.SourceGenerator Tasks:
1.  **Generate Invalidation Logic**:
    *   For methods marked with `[CacheInvalidate]`, generate code to call `ICacheManager.InvalidateByTagsAsync` with the specified tags.
2.  **Compile-time Validation**:
    *   Implement Roslyn analyzers to validate correct usage of `[Cache]` and `[CacheInvalidate]` attributes (e.g., `[Cache]` on non-virtual methods without interface, `[CacheInvalidate]` on non-async methods if it's meant for async invalidation).
    *   Provide meaningful diagnostics (warnings/errors).

## Phase 3: Production Readiness & Refinements

**Objective**: Enhance robustness, observability, and developer experience for production environments.

### MethodCache.Core Tasks:
1.  **Refine Resilience**:
    *   Implement granular circuit breaker per-provider instance and per-operation type.
    *   Add configurable timeout management for cache operations.
2.  **Monitoring and Observability**:
    *   Implement `ICacheMetricsProvider` for collecting hit/miss ratios, latencies, error rates.
    *   Integrate with `Microsoft.Extensions.Logging` for structured logging with parameter redaction.
    *   Implement health checks for cache providers.
3.  **Key Generation Refinements**:
    *   Implement `JsonKeyGenerator` for human-readable keys.
4.  **`InMemoryCacheManager` Enhancements**:
    *   Implement full tag-based invalidation for the `InMemoryCacheManager`.
    *   Add basic eviction policies (e.g., LRU, TTL) for `InMemoryCacheManager` for testing purposes.
5.  **Async Best Practices**:
    *   Ensure all internal awaits use `ConfigureAwait(false)`.
    *   Properly handle `ValueTask<T>` return types.

### MethodCache.SourceGenerator Tasks:
1.  **Refine Generated Code**:
    *   Optimize generated code for readability and performance.
    *   Ensure proper cancellation token propagation.
2.  **Support for Different Interception Modes**:
    *   Fully implement generation for both interface-based decoration and inheritance-based proxying.
