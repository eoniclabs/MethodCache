# PP-006 Legacy Removal Plan
## Delete CacheMethodSettings and Legacy Infrastructure

**Status:** Ready for implementation
**Created:** 2025-10-11
**Related:** [POLICY_PIPELINE_CONSOLIDATION_PLAN.md](./POLICY_PIPELINE_CONSOLIDATION_PLAN.md)

---

## Executive Summary

With PP-005 complete, all runtime code now uses `CacheRuntimeDescriptor` and `CachePolicy`. PP-006 removes the legacy `CacheMethodSettings` infrastructure entirely, completing the policy pipeline consolidation.

### Scope:
- **Delete** `CacheMethodSettings` class and extensions
- **Remove** legacy `ICacheManager` overloads
- **Remove** obsolete compatibility extensions (`ICacheKeyGeneratorCompatExtensions`)
- **Remove** conversion methods (`ToCacheMethodSettings`, `FromLegacySettings`)
- **Clean up** all `[Obsolete]` markers that reference removed APIs
- **Update** documentation to reflect policy-only architecture

---

## Current State Analysis

### Usage Count (as of PP-006 start):
- **MethodCache.Core:** 55 references to `CacheMethodSettings`
- **Key files:**
  - `CacheMethodSettings.cs` (definition)
  - `CacheMethodSettingsExtensions.cs` (Core & ETags)
  - `ICacheManager.cs` (legacy overloads)
  - `CacheRuntimeDescriptor.cs` (ToCacheMethodSettings, compat extensions)
  - `CachePolicyConversion.cs` (ToCacheMethodSettings)
  - All cache manager implementations (legacy overloads)

### Compatibility Layer (added in PP-005):
All marked `[Obsolete]` with "Will be removed in v4.0.0":
- `ICacheKeyGeneratorCompatExtensions.GenerateKey(settings)`
- `CacheRuntimeOptions.FromLegacySettings(settings)`
- `CacheRuntimeDescriptor.ToCacheMethodSettings()`
- `CacheMethodSettingsExtensions.GetETagMetadata(settings)` (Core)
- `CacheMethodSettingsExtensions.GetETagSettings(settings)` (ETags)

---

## Removal Strategy

### Phase 1: Remove Legacy ICacheManager Overloads
**Goal:** Remove all `ICacheManager` methods that accept `CacheMethodSettings`

#### 1.1 Interface Definition

**File:** `MethodCache.Core/Abstractions/ICacheManager.cs`

**Remove these overloads:**
```csharp
Task<T> GetOrCreateAsync<T>(
    string methodName,
    object[] args,
    Func<Task<T>> factory,
    CacheMethodSettings settings,          // ← REMOVE
    ICacheKeyGenerator keyGenerator,
    bool requireIdempotent);

ValueTask<T?> TryGetAsync<T>(
    string methodName,
    object[] args,
    CacheMethodSettings settings,          // ← REMOVE
    ICacheKeyGenerator keyGenerator);
```

**Keep these (descriptor-based):**
```csharp
Task<T> GetOrCreateAsync<T>(
    string methodName,
    object[] args,
    Func<Task<T>> factory,
    CacheRuntimeDescriptor descriptor,     // ✓ KEEP
    ICacheKeyGenerator keyGenerator);

ValueTask<T?> TryGetAsync<T>(
    string methodName,
    object[] args,
    CacheRuntimeDescriptor descriptor,     // ✓ KEEP
    ICacheKeyGenerator keyGenerator);
```

---

#### 1.2 Cache Manager Implementations

Remove settings-based overloads from:

1. **InMemoryCacheManager** (`MethodCache.Core/Runtime/Defaults/InMemoryCacheManager.cs`)
2. **HybridCacheManager** (`MethodCache.Core/Storage/Coordination/HybridCacheManager.cs`)
3. **MockCacheManager** (`MethodCache.Core/Runtime/Defaults/MockCacheManager.cs`)
4. **NoOpCacheManager** (`MethodCache.Core/Runtime/Defaults/NoOpCacheManager.cs`)
5. **Any Redis/storage providers** that implement `ICacheManager`

**Pattern to search:**
```bash
grep -n "GetOrCreateAsync.*CacheMethodSettings\|TryGetAsync.*CacheMethodSettings" --include="*.cs" -r .
```

---

### Phase 2: Remove ICacheKeyGenerator Compatibility Layer
**Goal:** Remove obsolete extension method and related helpers

#### 2.1 Remove ICacheKeyGeneratorCompatExtensions

**File:** `MethodCache.Core/Runtime/CacheRuntimeDescriptor.cs`

**Remove entire class:**
```csharp
[Obsolete("...")]
public static class ICacheKeyGeneratorCompatExtensions
{
    public static string GenerateKey(
        this ICacheKeyGenerator generator,
        string methodName,
        object[] args,
        CacheMethodSettings settings) { ... }
}
```

---

#### 2.2 Remove CacheRuntimeOptions.FromLegacySettings

**File:** `MethodCache.Core/Runtime/CacheRuntimeOptions.cs`

**Remove method:**
```csharp
[Obsolete("This is a migration helper and will be removed in v4.0.0")]
internal static CacheRuntimeOptions FromLegacySettings(
    Configuration.CacheMethodSettings settings) { ... }
```

---

### Phase 3: Remove CacheMethodSettings Core Infrastructure
**Goal:** Delete the `CacheMethodSettings` class and all direct extensions

#### 3.1 Delete CacheMethodSettings Class

**File:** `MethodCache.Core/Configuration/CacheMethodSettings.cs`

**Action:** Delete entire file

**Before deleting, verify no non-obsolete references:**
```bash
grep -r "new CacheMethodSettings\|: CacheMethodSettings" --include="*.cs" . | grep -v "Obsolete\|\.Tests"
```

---

#### 3.2 Remove CacheMethodSettings Extension Methods

**File:** `MethodCache.Core/Configuration/CacheMethodSettingsExtensions.cs`

**Remove obsolete methods:**
```csharp
[Obsolete("Use GetETagMetadata on CacheRuntimeDescriptor or CachePolicy...")]
public static ETagMetadata? GetETagMetadata(this CacheMethodSettings settings) { ... }

public static void SetETagMetadata(this CacheMethodSettings settings, ETagMetadata metadata) { ... }

public static void MergeWithDefaultETagMetadata(this CacheMethodSettings settings, ETagMetadata defaults) { ... }
```

**Keep new methods:**
- `GetETagMetadata(CacheRuntimeDescriptor)`
- `GetETagMetadata(CachePolicy)`
- All helper methods (`ParseETagMetadataFromPolicyKeys`, etc.)

**Consider:** Rename file to `ETagMetadataExtensions.cs` since it no longer extends CacheMethodSettings

---

**File:** `MethodCache.ETags/Configuration/CacheMethodSettingsExtensions.cs`

**Remove obsolete method:**
```csharp
[Obsolete("Use GetETagSettings on CacheRuntimeDescriptor or CachePolicy...")]
public static ETagSettings? GetETagSettings(this CacheMethodSettings settings) { ... }
```

**Keep:**
- `GetETagSettings(CacheRuntimeDescriptor)`
- `GetETagSettings(CachePolicy)`
- `ConvertToETagSettings(ETagMetadata)` helper

**Consider:** Rename file to `ETagSettingsExtensions.cs`

---

#### 3.3 Remove Conversion Methods

**File:** `MethodCache.Core/PolicyPipeline/Model/CachePolicyConversion.cs`

**Remove:**
```csharp
public static CacheMethodSettings ToCacheMethodSettings(CachePolicy policy) { ... }
private static void ApplyMetadata(CacheMethodSettings settings, ...) { ... }
```

**Action:** Likely delete entire file (verify no other methods exist)

**File:** `MethodCache.Core/Runtime/CacheRuntimeDescriptor.cs`

**Remove method:**
```csharp
public CacheMethodSettings ToCacheMethodSettings() { ... }
```

**Remove field:**
```csharp
private readonly CacheMethodSettings? _legacySettings;
```

**Update constructors:** Remove `legacySettings` parameter from:
- Private constructor
- `FromPolicy` method
- `FromPolicyDraft` method

---

### Phase 4: Update Tests
**Goal:** Remove or update tests that reference legacy APIs

#### 4.1 Find and Update Test Files

**Search pattern:**
```bash
grep -r "CacheMethodSettings" --include="*Tests.cs" -l
```

**Expected test files to update:**
- `MethodCache.ETags.Tests/Configuration/CacheMethodSettingsExtensionsTests.cs`
- Any integration tests using settings-based APIs

**Action:**
1. Remove tests for obsolete methods
2. Keep tests for descriptor/policy-based methods
3. Update any tests that construct `CacheMethodSettings` for test data to use `CachePolicy` + `CacheRuntimeDescriptor` instead

---

#### 4.2 Update Sample Apps

**Files found:**
- `MethodCache.SampleApp/Services/ConfigDrivenCacheService.cs`
- `MethodCache.SampleApp/Infrastructure/CustomKeyGenerator.cs`

**Action:**
1. Update custom key generators to implement descriptor-based interface
2. Update any manual cache manager calls to use descriptors

---

### Phase 5: Clean Up Obsolete Markers
**Goal:** Remove `[Obsolete]` attributes that reference removed types

**Search pattern:**
```bash
grep -r "\[Obsolete.*CacheMethodSettings" --include="*.cs"
```

**Action:**
- Remove or update obsolete markers that reference `CacheMethodSettings`
- If the obsolete method itself is being removed, remove the whole method
- If the obsolete method is staying (different reason), update message to not reference CacheMethodSettings

---

### Phase 6: Update Documentation
**Goal:** Remove all references to legacy configuration model

#### 6.1 Update Developer Documentation

**Files to update:**
- `docs/developer/POLICY_PIPELINE_CONSOLIDATION_PLAN.md` - Mark PP-006 complete
- `docs/developer/FOLDER_STRUCTURE.md` - Remove `CacheMethodSettings` references
- `docs/developer/RefactoringRecommendations.md` - Remove legacy migration notes

**Action:**
- Search for `CacheMethodSettings` in all .md files
- Replace with `CachePolicy` / `CacheRuntimeDescriptor` guidance
- Update architecture diagrams if any

---

#### 6.2 Update User Documentation

**Search:**
```bash
grep -r "CacheMethodSettings" --include="*.md" docs/
```

**Action:**
- Update configuration examples to show descriptor-based APIs
- Add migration guide for external users upgrading from v3.x
- Update API reference if auto-generated

---

#### 6.3 Create Migration Guide

**File:** `docs/MIGRATION_V3_TO_V4.md` (new)

**Contents:**
```markdown
# Migration Guide: v3.x to v4.0

## Breaking Changes

### Removed: CacheMethodSettings

The legacy `CacheMethodSettings` class has been removed. All configuration now uses `CachePolicy` and `CacheRuntimeDescriptor`.

**If you have custom ICacheManager implementations:**

Before (v3.x):
\`\`\`csharp
Task<T> GetOrCreateAsync<T>(
    string methodName,
    object[] args,
    Func<Task<T>> factory,
    CacheMethodSettings settings,
    ICacheKeyGenerator keyGenerator,
    bool requireIdempotent);
\`\`\`

After (v4.0):
\`\`\`csharp
Task<T> GetOrCreateAsync<T>(
    string methodName,
    object[] args,
    Func<Task<T>> factory,
    CacheRuntimeDescriptor descriptor,
    ICacheKeyGenerator keyGenerator);
\`\`\`

**If you have custom ICacheKeyGenerator implementations:**

Before (v3.x):
\`\`\`csharp
string GenerateKey(string methodName, object[] args, CacheMethodSettings settings);
\`\`\`

After (v4.0):
\`\`\`csharp
string GenerateKey(string methodName, object[] args, CacheRuntimeDescriptor descriptor);
\`\`\`

Access properties via descriptor:
- `settings.Version` → `descriptor.Version`
- `settings.Tags` → `descriptor.Tags`
- `settings.Duration` → `descriptor.Duration`
- `settings.SlidingExpiration` → `descriptor.RuntimeOptions.SlidingExpiration`
- `settings.StampedeProtection` → `descriptor.RuntimeOptions.StampedeProtection`

**ETag Extensions:**

Before (v3.x):
\`\`\`csharp
var etagSettings = settings.GetETagSettings();
\`\`\`

After (v4.0):
\`\`\`csharp
var etagSettings = descriptor.GetETagSettings();
// or
var etagSettings = policy.GetETagSettings();
\`\`\`

## Timeline

- **v3.5** (PP-005): Obsolete warnings introduced, new APIs available
- **v4.0** (PP-006): Legacy APIs removed

## Support

For questions or issues, please file a GitHub issue.
```

---

### Phase 7: Final Verification
**Goal:** Ensure clean removal with no lingering references

#### 7.1 Verification Checklist

Run these checks before marking PP-006 complete:

```bash
# 1. No CacheMethodSettings references (except in migration docs)
grep -r "CacheMethodSettings" --include="*.cs" . | grep -v "Migration\|CHANGELOG\|docs/"

# 2. No obsolete attributes referencing removed types
grep -r "\[Obsolete.*CacheMethodSettings\|Obsolete.*v4\.0\.0" --include="*.cs"

# 3. All ICacheManager implementations updated
grep -r "class.*: ICacheManager\|class.*: IMemoryCache" --include="*.cs" -A 10 | grep "CacheMethodSettings"

# 4. Build succeeds
dotnet build --no-incremental

# 5. All tests pass
dotnet test --no-build
```

---

#### 7.2 Expected Build Warnings

After PP-006, these warnings should **disappear**:
- ✅ CS0618 warnings about obsolete `GenerateKey(settings)` method
- ✅ CS0618 warnings about obsolete `GetETagMetadata(settings)` method
- ✅ CS0618 warnings about obsolete `GetETagSettings(settings)` method
- ✅ CS0618 warnings about obsolete `FromLegacySettings` method

---

## Implementation Order

1. ✅ **Phase 1** - Remove legacy ICacheManager overloads
2. ✅ **Phase 2** - Remove ICacheKeyGenerator compatibility layer
3. ✅ **Phase 3** - Remove CacheMethodSettings core infrastructure
4. ✅ **Phase 4** - Update tests and samples
5. ✅ **Phase 5** - Clean up obsolete markers
6. ✅ **Phase 6** - Update documentation
7. ✅ **Phase 7** - Final verification

**Estimated effort:** 3-4 hours

---

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| External packages still reference removed APIs | HIGH | Release as major version (v4.0); provide migration guide |
| Missing test coverage exposes runtime issues | MEDIUM | Run full test suite before/after; verify integration tests pass |
| Documentation lag causes confusion | LOW | Update docs in same PR; link migration guide prominently |
| Hidden usages in downstream projects | MEDIUM | Publish pre-release v4.0-beta for feedback period |

---

## Success Criteria

- [ ] Zero references to `CacheMethodSettings` in src/ (except migration docs)
- [ ] `dotnet build` succeeds with 0 errors, 0 obsolete warnings
- [ ] `dotnet test` passes 100%
- [ ] Documentation updated and reviewed
- [ ] Migration guide created
- [ ] CHANGELOG.md updated with breaking changes section
- [ ] PP-006 marked complete in `POLICY_PIPELINE_CONSOLIDATION_PLAN.md`

---

## Next Steps

After PP-006 completion:
1. Tag release as **v4.0.0-beta.1**
2. Solicit feedback from community/internal users
3. Address any migration issues discovered
4. Release **v4.0.0** stable
5. Celebrate! 🎉 The policy pipeline consolidation is complete!
