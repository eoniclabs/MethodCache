# PP-005 Remaining Migration Plan
## Migrate Key Generators and Runtime Helpers off CacheMethodSettings

**Status:** Ready for implementation
**Created:** 2025-10-11
**Related:** [POLICY_PIPELINE_CONSOLIDATION_PLAN.md](./POLICY_PIPELINE_CONSOLIDATION_PLAN.md)

---

## Executive Summary

This document outlines the plan to complete PP-005 by migrating the remaining components off `CacheMethodSettings`:
- **Key Generators** (`ICacheKeyGenerator` implementations and call sites)
- **ETag Helpers** (metadata extraction and settings conversion)
- **Stampede Protection** (remove legacy code paths in cache managers)
- **Distributed Locks** (remove legacy code paths in cache managers)

All components have new infrastructure in place via `CacheRuntimeDescriptor` and `CacheRuntimeOptions`. This task involves:
1. Removing default implementations that call legacy methods
2. Updating all call sites to use descriptor-based APIs
3. Providing migration helpers for external consumers
4. Cleaning up legacy code paths

---

## Current State Analysis

### 1. Key Generators (`ICacheKeyGenerator`)

**Interface Definition** (`MethodCache.Core/Abstractions/ICacheKeyGenerator.cs:34-62`)
```csharp
public interface ICacheKeyGenerator
{
    // Legacy method - still the primary implementation target
    string GenerateKey(string methodName, object[] args, CacheMethodSettings settings);

    // New method - has default implementation calling legacy via descriptor.ToCacheMethodSettings()
    string GenerateKey(string methodName, object[] args, CacheRuntimeDescriptor descriptor)
        => GenerateKey(methodName, args, descriptor.ToCacheMethodSettings());
}
```

**Implementation Status:**
| Generator | Legacy Method | Descriptor Method | Status |
|-----------|--------------|-------------------|---------|
| `FastHashKeyGenerator` | ✅ Implemented | ✅ Implemented (calls private shared method) | **Ready** |
| `JsonKeyGenerator` | ✅ Implemented | ❌ Missing | **Needs work** |
| `MessagePackKeyGenerator` | ✅ Implemented | ❌ Missing | **Needs work** |
| `SmartKeyGenerator` | ✅ Implemented | ❌ Needs verification | **Check** |
| `FixedKeyGenerator` (internal, in `CacheManagerExtensions`) | ✅ Implemented | ❌ Missing | **Needs work** |

**Note:** Fluent helper key generators already use descriptors after earlier PP-005 work; focus is on the built-in generators and the internal `FixedKeyGenerator` in `CacheManagerExtensions`.

**Call Sites:**
- `CacheManagerExtensions.cs:71, 173, 676` - All call `GenerateKey(methodName, args, settings)`
- Source-generated decorators (need verification)

**Issue:** The default implementation in the interface allows the legacy method to remain primary, delaying full migration.

---

### 2. ETag Helpers

**Current Implementation:**

`MethodCache.Core/Configuration/CacheMethodSettingsExtensions.cs:17-22`
```csharp
public static ETagMetadata? GetETagMetadata(this CacheMethodSettings settings)
{
    return settings.Metadata.TryGetValue(ETagMetadataKey, out var value)
        ? value as ETagMetadata
        : null;
}
```

`MethodCache.ETags/Configuration/CacheMethodSettingsExtensions.cs:9-34`
```csharp
public static ETagSettings? GetETagSettings(this CacheMethodSettings settings)
{
    var metadata = settings.GetETagMetadata();
    // ... converts ETagMetadata to ETagSettings
}
```

**Issue:** Both helpers extract from `CacheMethodSettings.Metadata`, but:
- `ETagMetadata` is already stored in `CachePolicy.Metadata` (string-based dictionary)
- `CacheRuntimeDescriptor` exposes `Metadata` property
- Need descriptor/policy-based versions of these extensions

---

### 3. Stampede Protection & Distributed Locks

**Current State in `InMemoryCacheManager`:**

`MethodCache.Core/Runtime/Defaults/InMemoryCacheManager.cs:384-386` (Legacy path)
```csharp
settings.StampedeProtection,
settings.DistributedLock,
settings.Metrics);
```

`MethodCache.Core/Runtime/Defaults/InMemoryCacheManager.cs:401-403` (New path)
```csharp
options.StampedeProtection,
options.DistributedLock,
options.Metrics);
```

**Status:**
- ✅ `CacheRuntimeOptions` already contains these properties
- ✅ `InMemoryCacheManager` already consumes from both paths
- ❌ Legacy path still exists and needs removal

---

## Migration Tasks

### Task 1: Update `ICacheKeyGenerator` Interface

**Goal:** Make the descriptor-based method the primary interface method.

**Changes:**
1. **Reverse the default implementation:**
   ```csharp
   public interface ICacheKeyGenerator
   {
       // Make descriptor-based method primary (no default implementation)
       string GenerateKey(string methodName, object[] args, CacheRuntimeDescriptor descriptor);

       // Provide backward compatibility via extension method (marked obsolete)
       // [Obsolete] - to be added in extension class
       // string GenerateKey(string methodName, object[] args, CacheMethodSettings settings);
   }
   ```

2. **Create compatibility extension method:**
   Add to `MethodCache.Core/Runtime/CacheRuntimeDescriptor.cs` (near the existing descriptor helpers for easy deletion in PP-006):
   ```csharp
   // Add to end of CacheRuntimeDescriptor.cs file as a companion extension class

   /// <summary>
   /// Backward-compatibility extensions for ICacheKeyGenerator during migration to descriptors.
   /// Will be removed in v4.0.0.
   /// </summary>
   public static class ICacheKeyGeneratorCompatExtensions
   {
       [Obsolete("Use GenerateKey overload with CacheRuntimeDescriptor. Will be removed in v4.0.0")]
       public static string GenerateKey(
           this ICacheKeyGenerator generator,
           string methodName,
           object[] args,
           CacheMethodSettings settings)
       {
           var descriptor = CacheRuntimeDescriptor.FromPolicy(
               methodName,
               CachePolicyConversion.FromCacheMethodSettings(settings),
               CachePolicyFields.All, // Conservative - all fields set
               null,
               CacheRuntimeOptions.FromLegacySettings(settings),
               settings);
           return generator.GenerateKey(methodName, args, descriptor);
       }
   }
   ```

3. **Add helper to `CacheRuntimeOptions`:**
   ```csharp
   public static CacheRuntimeOptions FromLegacySettings(CacheMethodSettings settings)
   {
       return new CacheRuntimeOptions(
           settings.SlidingExpiration,
           settings.RefreshAhead,
           settings.StampedeProtection,
           settings.DistributedLock,
           settings.Metrics);
   }
   ```

**Files:**
- `MethodCache.Core/Abstractions/ICacheKeyGenerator.cs`
- `MethodCache.Core/Runtime/CacheRuntimeDescriptor.cs` (add compat extension class)
- `MethodCache.Core/Runtime/CacheRuntimeOptions.cs`

---

### Task 2: Update Key Generator Implementations

**Goal:** Implement descriptor-based method in all generators and make legacy method call it.

**Focus Areas:**
- Built-in generators: `JsonKeyGenerator`, `MessagePackKeyGenerator`, `SmartKeyGenerator` (verify if exists)
- Internal helper: `FixedKeyGenerator` in `CacheManagerExtensions`
- Note: Fluent helper generators already migrated in earlier PP-005 work

#### 2.1 `JsonKeyGenerator`

**Current:** Only legacy method implemented
**Action:** Add descriptor-based implementation

```csharp
public class JsonKeyGenerator : ICacheKeyGenerator
{
    // New primary implementation
    public string GenerateKey(string methodName, object[] args, CacheRuntimeDescriptor descriptor)
    {
        var keyBuilder = new StringBuilder();
        keyBuilder.Append(methodName);

        if (descriptor.Version.HasValue)
            keyBuilder.Append($"_v{descriptor.Version.Value}");

        foreach (var arg in args)
        {
            if (arg is ICacheKeyProvider keyProvider)
            {
                keyBuilder.Append($"_{keyProvider.CacheKeyPart}");
            }
            else
            {
                var serializedArg = JsonSerializer.Serialize(arg,
                    new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.Preserve });
                keyBuilder.Append($"_{serializedArg}");
            }
        }

        using (var sha256 = SHA256.Create())
        {
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(keyBuilder.ToString()));
            var base64Hash = Convert.ToBase64String(hash);
            if (descriptor.Version.HasValue)
                return $"{base64Hash}_v{descriptor.Version.Value}";
            return base64Hash;
        }
    }
}
```

**File:** `MethodCache.Core/Keys/JsonKeyGenerator.cs`

---

#### 2.2 `MessagePackKeyGenerator`

**Current:** Only legacy method implemented
**Action:** Add descriptor-based implementation

```csharp
public class MessagePackKeyGenerator : ICacheKeyGenerator
{
    // New primary implementation
    public string GenerateKey(string methodName, object[] args, CacheRuntimeDescriptor descriptor)
    {
        using (var sha256 = SHA256.Create())
        {
            var methodBytes = Encoding.UTF8.GetBytes(methodName);
            sha256.TransformBlock(methodBytes, 0, methodBytes.Length, null, 0);

            if (descriptor.Version.HasValue)
            {
                var versionBytes = BitConverter.GetBytes(descriptor.Version.Value);
                sha256.TransformBlock(versionBytes, 0, versionBytes.Length, null, 0);
            }

            foreach (var arg in args)
            {
                if (arg is ICacheKeyProvider keyProvider)
                {
                    var keyPartBytes = Encoding.UTF8.GetBytes(keyProvider.CacheKeyPart);
                    sha256.TransformBlock(keyPartBytes, 0, keyPartBytes.Length, null, 0);
                }
                else
                {
                    var serializedArg = MessagePackSerializer.Typeless.Serialize(arg);
                    sha256.TransformBlock(serializedArg, 0, serializedArg.Length, null, 0);
                }
            }

            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToBase64String(sha256.Hash!);
        }
    }
}
```

**File:** `MethodCache.Core/Keys/MessagePackKeyGenerator.cs`

---

#### 2.3 `FixedKeyGenerator` (internal in `CacheManagerExtensions`)

**Current:** Only legacy method implemented
**Action:** Add descriptor-based implementation
**Note:** This is the internal helper in `CacheManagerExtensions`, not fluent helper generators (which already use descriptors)

```csharp
private sealed class FixedKeyGenerator : ICacheKeyGenerator
{
    private readonly string _key;
    private readonly int? _version;

    public FixedKeyGenerator(string key, int? version = null)
    {
        _key = key ?? throw new ArgumentNullException(nameof(key));
        _version = version;
    }

    public string GenerateKey(string methodName, object[] args, CacheRuntimeDescriptor descriptor)
    {
        var effectiveVersion = _version ?? descriptor.Version;
        if (effectiveVersion.HasValue)
        {
            return $"{_key}::v{effectiveVersion.Value}";
        }
        return _key;
    }
}
```

**File:** `MethodCache.Core/Extensions/CacheManagerExtensions.cs`

---

#### 2.4 `SmartKeyGenerator`

**Current:** Only legacy method implemented
**Action:** Add descriptor-based implementation following same pattern as `FastHashKeyGenerator`

```csharp
public class SmartKeyGenerator : ICacheKeyGenerator
{
    // New primary implementation
    public string GenerateKey(string methodName, object[] args, CacheRuntimeDescriptor descriptor)
        => GenerateKey(methodName, args, descriptor.Version);

    // Private shared implementation
    private static string GenerateKey(string methodName, object[] args, int? version)
    {
        // Existing logic adapted from settings-based method
        // ... implementation details
    }
}
```

**File:** `MethodCache.Core/Keys/SmartKeyGenerator.cs`

---

### Task 3: Update `CacheManagerExtensions` Call Sites

**Goal:** Change all `GenerateKey` calls to use descriptor instead of settings.

**Locations:** Lines 71, 173, 676 in `CacheManagerExtensions.cs`

**Before:**
```csharp
var cacheKey = effectiveKeyGenerator.GenerateKey(methodName, args, settings);
```

**After:**
```csharp
var cacheKey = effectiveKeyGenerator.GenerateKey(methodName, args, descriptor);
```

**Note:** All three call sites already have `descriptor` in scope, so this is a straightforward replacement.

**File:** `MethodCache.Core/Extensions/CacheManagerExtensions.cs`

---

### Task 4: Create ETag Helper Extensions for Descriptor/Policy

**Goal:** Provide descriptor-based ETag helpers and obsolete settings-based ones.

#### 4.1 Core Extensions (`MethodCache.Core`)

**Add to `CacheMethodSettingsExtensions.cs` or create new `CachePolicyMetadataExtensions.cs`:**

```csharp
namespace MethodCache.Core.Configuration
{
    public static class CachePolicyMetadataExtensions
    {
        private const string ETagMetadataKey = "MethodCache.ETags.Metadata";

        // Extract from CacheRuntimeDescriptor
        public static ETagMetadata? GetETagMetadata(this CacheRuntimeDescriptor descriptor)
        {
            if (descriptor?.Metadata == null) return null;
            return ParseETagMetadata(descriptor.Metadata);
        }

        // Extract from CachePolicy
        public static ETagMetadata? GetETagMetadata(this CachePolicy policy)
        {
            if (policy?.Metadata == null) return null;
            return ParseETagMetadata(policy.Metadata);
        }

        // Parse from string-based metadata dictionary
        private static ETagMetadata? ParseETagMetadata(IReadOnlyDictionary<string, string?> metadata)
        {
            if (!metadata.TryGetValue(ETagMetadataKey, out var json) || string.IsNullOrEmpty(json))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<ETagMetadata>(json);
            }
            catch
            {
                return null;
            }
        }

        // Obsolete existing method
        [Obsolete("Use GetETagMetadata on CacheRuntimeDescriptor or CachePolicy. Will be removed in v4.0.0")]
        public static ETagMetadata? GetETagMetadata(this CacheMethodSettings settings)
        {
            // Keep existing implementation for backward compatibility
            return settings.Metadata.TryGetValue(ETagMetadataKey, out var value)
                ? value as ETagMetadata
                : null;
        }
    }
}
```

**Files:**
- `MethodCache.Core/Configuration/CachePolicyMetadataExtensions.cs` (new) OR
- `MethodCache.Core/Configuration/CacheMethodSettingsExtensions.cs` (add to existing)

---

#### 4.2 ETags Package Extensions

**Update `MethodCache.ETags/Configuration/CacheMethodSettingsExtensions.cs`:**

```csharp
namespace MethodCache.ETags.Configuration
{
    public static class CacheRuntimeDescriptorExtensions
    {
        // New descriptor-based method
        public static ETagSettings? GetETagSettings(this CacheRuntimeDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));

            var metadata = descriptor.GetETagMetadata(); // Uses core extension
            if (metadata == null) return null;

            return ConvertToETagSettings(metadata);
        }

        // New policy-based method
        public static ETagSettings? GetETagSettings(this CachePolicy policy)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));

            var metadata = policy.GetETagMetadata(); // Uses core extension
            if (metadata == null) return null;

            return ConvertToETagSettings(metadata);
        }

        private static ETagSettings ConvertToETagSettings(ETagMetadata metadata)
        {
            var strategy = ETagGenerationStrategy.ContentHash;
            if (!string.IsNullOrWhiteSpace(metadata.Strategy)
                && Enum.TryParse(metadata.Strategy, out ETagGenerationStrategy parsedStrategy))
            {
                strategy = parsedStrategy;
            }

            return new ETagSettings
            {
                Strategy = strategy,
                IncludeParametersInETag = metadata.IncludeParametersInETag ?? true,
                ETagGeneratorType = metadata.ETagGeneratorType,
                Metadata = metadata.Metadata,
                UseWeakETag = metadata.UseWeakETag ?? false,
                CacheDuration = metadata.CacheDuration
            };
        }

        // Obsolete existing method
        [Obsolete("Use GetETagSettings on CacheRuntimeDescriptor or CachePolicy. Will be removed in v4.0.0")]
        public static ETagSettings? GetETagSettings(this CacheMethodSettings settings)
        {
            // Keep existing implementation
            var metadata = settings.GetETagMetadata();
            if (metadata == null) return null;
            return ConvertToETagSettings(metadata);
        }
    }
}
```

**File:** `MethodCache.ETags/Configuration/CacheMethodSettingsExtensions.cs` (update) or new file

---

### Task 5: Remove Legacy Paths from Cache Managers

**Goal:** Remove `CacheMethodSettings` consumption in cache managers.

#### 5.1 `InMemoryCacheManager`

**Location:** `MethodCache.Core/Runtime/Defaults/InMemoryCacheManager.cs:378-387`

**Before:**
```csharp
private CacheEntryPolicy ToCacheEntryPolicy(CacheMethodSettings settings)
{
    return new CacheEntryPolicy(
        settings.Duration,
        settings.SlidingExpiration,
        settings.RefreshAhead,
        settings.StampedeProtection,
        settings.DistributedLock,
        settings.Metrics);
}
```

**After:**
```csharp
// Remove this method entirely - no longer needed
// All callers should use ToCacheEntryPolicy(CacheRuntimeDescriptor, CacheRuntimeOptions)
```

**Search for call sites:** `grep -n "ToCacheEntryPolicy.*settings" InMemoryCacheManager.cs`

**File:** `MethodCache.Core/Runtime/Defaults/InMemoryCacheManager.cs`

---

#### 5.2 Other Cache Managers

**Search and update:**
1. `HybridCacheManager` (if exists)
2. Redis providers
3. Any other storage implementations

**Pattern to search:**
```bash
grep -r "settings\.StampedeProtection\|settings\.DistributedLock" --include="*.cs"
```

---

### Task 6: Update Tests

**Goal:** Ensure all tests work with new APIs and add coverage for descriptor-based methods.

#### 6.1 Key Generator Tests

**Add descriptor-based tests to:**
- `JsonKeyGenerator` tests
- `MessagePackKeyGenerator` tests
- `FastHashKeyGenerator` tests (verify both paths still work)

**Example test pattern:**
```csharp
[Fact]
public void GenerateKey_WithDescriptor_ProducesConsistentKey()
{
    // Arrange
    var generator = new JsonKeyGenerator();
    var policy = new CachePolicy { Version = 1 };
    var descriptor = CacheRuntimeDescriptor.FromPolicy(
        "TestMethod",
        policy,
        CachePolicyFields.Version);
    var args = new object[] { 123, "test" };

    // Act
    var key1 = generator.GenerateKey("TestMethod", args, descriptor);
    var key2 = generator.GenerateKey("TestMethod", args, descriptor);

    // Assert
    Assert.Equal(key1, key2);
    Assert.Contains("_v1", key1);
}
```

---

#### 6.2 ETag Helper Tests

**Update:** `MethodCache.ETags.Tests/Configuration/CacheMethodSettingsExtensionsTests.cs`

**Add tests:**
```csharp
[Fact]
public void GetETagSettings_FromDescriptor_ReturnsCorrectSettings()
{
    // Arrange
    var metadata = new Dictionary<string, string?>
    {
        ["MethodCache.ETags.Metadata"] = JsonSerializer.Serialize(new ETagMetadata
        {
            Strategy = "ContentHash",
            UseWeakETag = true
        })
    };
    var policy = new CachePolicy { Metadata = metadata };
    var descriptor = CacheRuntimeDescriptor.FromPolicy("Test", policy, CachePolicyFields.None, metadata);

    // Act
    var settings = descriptor.GetETagSettings();

    // Assert
    Assert.NotNull(settings);
    Assert.Equal(ETagGenerationStrategy.ContentHash, settings.Strategy);
    Assert.True(settings.UseWeakETag);
}
```

---

### Task 7: Update Documentation

**Goal:** Document the changes and provide migration guidance.

#### 7.1 Migration Guide

**Create:** `docs/developer/MIGRATION_PP005_KEY_GENERATORS.md`

```markdown
# PP-005 Key Generator Migration Guide

## Breaking Changes

### `ICacheKeyGenerator` Interface

The descriptor-based method is now primary:

**Before:**
```csharp
public class MyKeyGenerator : ICacheKeyGenerator
{
    public string GenerateKey(string methodName, object[] args, CacheMethodSettings settings)
    {
        return $"{methodName}_{settings.Version}";
    }
}
```

**After:**
```csharp
public class MyKeyGenerator : ICacheKeyGenerator
{
    public string GenerateKey(string methodName, object[] args, CacheRuntimeDescriptor descriptor)
    {
        return $"{methodName}_{descriptor.Version}";
    }
}
```

### ETag Helpers

**Before:**
```csharp
var etagSettings = settings.GetETagSettings();
```

**After:**
```csharp
var etagSettings = descriptor.GetETagSettings();
// or
var etagSettings = policy.GetETagSettings();
```

## Compatibility

- Legacy overloads marked `[Obsolete]` in v3.x
- Obsolete APIs removed in v4.0.0
- Extension methods provide compatibility during transition
```

---

#### 7.2 Update POLICY_PIPELINE_CONSOLIDATION_PLAN.md

**Mark PP-005 as complete:**
```markdown
5. **PP-005 – Runtime override & consumption alignment** ✅ _2025-10-11_
   - ✅ Added policy-builder overload to `IRuntimeCacheConfigurator`
   - ✅ Introduced `CacheRuntimeDescriptor`
   - ✅ Updated cache managers to consume descriptors/runtime options
   - ✅ Migrated key generators off `CacheMethodSettings`
   - ✅ Migrated ETag helpers to descriptor-based APIs
   - ✅ Removed legacy stampede/lock consumption from cache managers
```

---

## Implementation Order

1. ✅ **Task 1** - Update `ICacheKeyGenerator` interface and create compatibility extensions
2. ✅ **Task 2** - Update all key generator implementations
3. ✅ **Task 3** - Update `CacheManagerExtensions` call sites
4. ✅ **Task 4** - Create ETag helper extensions for descriptor/policy
5. ✅ **Task 5** - Remove legacy paths from cache managers
6. ✅ **Task 6** - Update and add tests
7. ✅ **Task 7** - Update documentation

**Estimated effort:** 4-6 hours

---

## Testing Strategy

### Unit Tests
- Key generator descriptor methods (all implementations)
- ETag metadata extraction from descriptors/policies
- Cache manager policy consumption (no settings usage)

### Integration Tests
- Source-generated decorators with descriptor-based key generators
- ETag middleware with descriptor-based settings
- Stampede protection with `CacheRuntimeOptions`

### Regression Tests
- Verify obsolete APIs still work during transition
- Confirm backward compatibility extensions function correctly

---

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Breaking external custom key generators | HIGH | Provide obsolete compatibility extension; document migration path |
| Source generator needs updates | MEDIUM | Verify generated code uses descriptor-based APIs |
| ETag metadata serialization format | LOW | Use JSON for metadata storage; handle parsing errors gracefully |
| Test coverage gaps | MEDIUM | Add comprehensive descriptor-based tests before removing legacy code |

---

## Verification Checklist

- [ ] All built-in key generators implement descriptor-based method
- [ ] `CacheManagerExtensions` uses descriptor-based key generation
- [ ] ETag helpers work with descriptors and policies
- [ ] Cache managers removed settings-based stampede/lock code
- [ ] All tests pass
- [ ] No `settings.StampedeProtection` or `settings.DistributedLock` in cache managers
- [ ] No `GenerateKey(..., settings)` calls in runtime code (except obsolete compatibility)
- [ ] Documentation updated
- [ ] Migration guide created
- [ ] `POLICY_PIPELINE_CONSOLIDATION_PLAN.md` marked PP-005 complete

---

## Next Steps (PP-006)

Once PP-005 is complete, proceed to **PP-006: Legacy removal & doc refresh**:
1. Delete `CacheMethodSettings` entirely
2. Remove `CachePolicyConversion.ToCacheMethodSettings()`
3. Remove `CacheRuntimeDescriptor.ToCacheMethodSettings()`
4. Clean up all obsolete attributes
5. Final documentation refresh
