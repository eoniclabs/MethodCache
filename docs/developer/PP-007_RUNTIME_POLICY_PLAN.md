# PP-007: Introduce CacheRuntimePolicy as Definitive Runtime Representation

## Status: COMPLETE ✅

**Created**: October 11, 2025
**Completed**: October 12, 2025
**Target**: Complete modernization of runtime cache configuration

## Overview

Following the successful completion of PP-006 (which removed `CacheMethodSettings`), we now have `CacheRuntimeDescriptor` serving as a wrapper/adapter pattern. This proposal introduces `CacheRuntimePolicy` as the first-class, authoritative runtime policy object, completing the modernization of the MethodCache runtime architecture.

## Motivation

### Current State
- `CacheRuntimeDescriptor` feels like a wrapper/adapter over `CachePolicy`
- The name "descriptor" implies it's describing something else, not the thing itself
- Conversion steps still exist between resolver output and runtime consumption
- The architecture has a "bridging API" feel rather than a clean runtime model

### Problems
1. **Semantic Confusion**: "Descriptor" suggests metadata about a policy, not the policy itself
2. **Wrapper Overhead**: Extra indirection between resolved policy and runtime usage
3. **Mixed Responsibilities**: Combines policy data, runtime options, and metadata in an ad-hoc way
4. **Limited Extensibility**: Hard to add new runtime concerns without making the wrapper more complex

### Solution
Introduce `CacheRuntimePolicy` as the definitive, first-class runtime representation that:
- Packages all runtime needs in a single, immutable record
- Flows directly from resolver to cache managers without conversion
- Provides clear semantic meaning: this IS the runtime policy
- Supports future extensibility for new runtime concerns

## Design

### CacheRuntimePolicy Structure

```csharp
namespace MethodCache.Core.Runtime
{
    /// <summary>
    /// The definitive runtime representation of a cache policy, containing all
    /// information needed for cache operations at runtime.
    /// </summary>
    public sealed record CacheRuntimePolicy
    {
        // Identity
        public required string MethodId { get; init; }

        // Core Policy Data (from CachePolicy)
        public TimeSpan? Duration { get; init; }
        public IReadOnlyCollection<string> Tags { get; init; } = Array.Empty<string>();
        public int? Version { get; init; }
        public bool? RequireIdempotent { get; init; }
        public Type? KeyGeneratorType { get; init; }

        // Runtime Options (from CacheRuntimeOptions)
        public TimeSpan? SlidingExpiration { get; init; }
        public TimeSpan? RefreshAhead { get; init; }
        public StampedeProtectionSettings? StampedeProtection { get; init; }
        public DistributedLockSettings? DistributedLock { get; init; }

        // Metadata & Indexing
        public IReadOnlyDictionary<string, string?> Metadata { get; init; } =
            new Dictionary<string, string?>(StringComparer.Ordinal);
        public CachePolicyFields Fields { get; init; } = CachePolicyFields.None;

        // Computed Properties
        public bool HasDuration => Duration.HasValue && Duration.Value > TimeSpan.Zero;
        public bool HasTags => Tags.Count > 0;
        public bool HasVersion => Version.HasValue;
        public bool HasRuntimeOptions =>
            SlidingExpiration.HasValue ||
            RefreshAhead.HasValue ||
            StampedeProtection != null ||
            DistributedLock != null;

        // Factory Methods
        public static CacheRuntimePolicy FromResolverResult(PolicyResolutionResult result);
        public static CacheRuntimePolicy FromPolicy(string methodId, CachePolicy policy, CachePolicyFields fields);
        public static CacheRuntimePolicy Empty(string methodId);

        // Compatibility (temporary during migration)
        public static CacheRuntimePolicy FromDescriptor(CacheRuntimeDescriptor descriptor);
        public CacheRuntimeDescriptor ToDescriptor(); // For backward compatibility
    }
}
```

### Updated Interfaces

```csharp
// ICacheManager - Updated signature
public interface ICacheManager
{
    Task<T> GetOrCreateAsync<T>(
        string methodName,
        object[] args,
        Func<Task<T>> factory,
        CacheRuntimePolicy policy,  // Changed from CacheRuntimeDescriptor
        ICacheKeyGenerator keyGenerator);

    ValueTask<T?> TryGetAsync<T>(
        string methodName,
        object[] args,
        CacheRuntimePolicy policy,  // Changed from CacheRuntimeDescriptor
        ICacheKeyGenerator keyGenerator);
}

// ICacheKeyGenerator - Updated signature
public interface ICacheKeyGenerator
{
    string GenerateKey(
        string methodName,
        object[] args,
        CacheRuntimePolicy policy);  // Changed from CacheRuntimeDescriptor
}
```

## Implementation Plan

### Phase 1: Parallel Introduction (Non-Breaking)
1. ✅ Create `CacheRuntimePolicy` record with all necessary fields
2. ✅ Add factory methods for creating from various sources
3. ✅ Add bidirectional conversion with `CacheRuntimeDescriptor`
4. ✅ Update resolver to produce `CacheRuntimePolicy` internally

### Phase 2: Core Migration
1. ✅ Update `ICacheManager` to accept `CacheRuntimePolicy`
2. ✅ Update `ICacheKeyGenerator` to accept `CacheRuntimePolicy`
3. ✅ Update all built-in cache manager implementations
4. ✅ Update all built-in key generator implementations
5. ✅ Add compatibility overloads that accept `CacheRuntimeDescriptor` (marked obsolete)

### Phase 3: Ecosystem Updates
1. ✅ Update source generator to emit `CacheRuntimePolicy` references
2. ✅ Update ETag extensions to use `CacheRuntimePolicy`
3. ✅ Update OpenTelemetry instrumentation
4. ✅ Update all provider implementations (Redis, SQL Server, etc.)

### Phase 4: Deprecation
1. ✅ Mark `CacheRuntimeDescriptor` as `[Obsolete]`
2. ✅ Update all tests to use `CacheRuntimePolicy`
3. ✅ Update all example code
4. ⏳ Update documentation (waiting for PP-006 doc updates)

### Phase 5: Removal ✅ COMPLETE
1. ✅ Removed `CacheRuntimeDescriptor` entirely
2. ✅ Removed all compatibility overloads
3. ✅ Completed documentation cleanup

## Benefits

### Immediate Benefits
- **Semantic Clarity**: "Runtime Policy" clearly communicates purpose
- **Direct Flow**: Resolver → RuntimePolicy → CacheManager (no conversion)
- **Performance**: Single allocation, no wrapper overhead
- **Clean API**: Intuitive, self-documenting interface

### Long-term Benefits
- **Extensibility**: Easy to add new runtime concerns
- **Maintainability**: Single source of truth for runtime configuration
- **Future-proof**: Room for features like cache tiers, circuit breakers, etc.
- **Type Safety**: Immutable record with required fields

## Migration Guide

### For Library Consumers

```csharp
// Old (with CacheRuntimeDescriptor)
var descriptor = CacheRuntimeDescriptor.FromPolicy("method", policy, fields);
await cacheManager.GetOrCreateAsync(methodName, args, factory, descriptor, keyGen);

// New (with CacheRuntimePolicy)
var runtimePolicy = CacheRuntimePolicy.FromPolicy("method", policy, fields);
await cacheManager.GetOrCreateAsync(methodName, args, factory, runtimePolicy, keyGen);
```

### For Custom Implementations

```csharp
// Old cache manager
public class CustomCacheManager : ICacheManager
{
    public Task<T> GetOrCreateAsync<T>(..., CacheRuntimeDescriptor descriptor, ...)
    {
        var duration = descriptor.Duration;
        // ...
    }
}

// New cache manager
public class CustomCacheManager : ICacheManager
{
    public Task<T> GetOrCreateAsync<T>(..., CacheRuntimePolicy policy, ...)
    {
        var duration = policy.Duration;
        // ...
    }
}
```

## Success Criteria ✅ ALL COMPLETE

- [x] All code compiles with zero errors ✅
- [x] All tests pass ✅
- [x] No references to `CacheRuntimeDescriptor` in production code ✅
- [x] Performance benchmarks show no regression ✅
- [x] Documentation updated to reference `CacheRuntimePolicy` ✅
- [x] Migration guide published ✅
- [x] Schema file updated ✅
- [x] All test files updated ✅
- [x] All example code updated ✅

## Notes

This completes the modernization arc started in PP-005:
- PP-005: Migrated to policy pipeline architecture ✅
- PP-006: Removed legacy `CacheMethodSettings` ✅
- PP-007: Introduced definitive `CacheRuntimePolicy` ✅

The result is a clean, modern, and extensible runtime architecture ready for the next 5+ years of evolution.

## Completion Summary

**Date Completed**: October 12, 2025

### What Was Accomplished

1. **Complete Removal**: All `CacheRuntimeDescriptor` references removed from production code
2. **API Migration**: Updated all 12 cache manager implementations to use `CacheRuntimePolicy`
3. **Key Generators**: Updated all 7 key generator implementations
4. **Source Generator**: Fixed to emit `CacheRuntimePolicy` with correct enum field names
5. **Test Files**: Updated 50+ test files including ETags, Analyzers, and integration tests
6. **Documentation**: Updated all example files and schema to reference `CacheRuntimePolicy`
7. **Verification**:
   - Build: ✅ 0 errors, 0 warnings
   - Unit Tests: ✅ All passing (400+ tests)
   - No remaining `CacheRuntimeDescriptor` in *.cs files

### Breaking Changes

This was implemented as a breaking change:
- `CacheRuntimeDescriptor` type completely removed
- All compatibility overloads removed
- Direct migration path: Replace `CacheRuntimeDescriptor` with `CacheRuntimePolicy`

The architecture is now fully modernized with `CacheRuntimePolicy` as the single, definitive runtime representation.