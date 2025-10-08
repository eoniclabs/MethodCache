# Policy Pipeline Migration – Detailed Implementation Plan

This document refines the remaining work for Phase 7 and clarifies the relationship between configuration surfaces and the new policy pipeline. The goal is to keep all existing ways of configuring MethodCache (attributes, JSON/YAML, fluent/programmatic, runtime overrides) while routing them through the `PolicyResolver`/`IPolicyRegistry` infrastructure for consistent behavior and diagnostics.

## Design Principles
- **Preserve configuration input surfaces:** Attributes, JSON/YAML, programmatic builders, and runtime overrides remain the public entry points.
- **Route all sources through the policy pipeline:** Each input produces `PolicySourceRegistration` entries consumed by `PolicyResolver`.
- **Expose the merged view via diagnostics:** `PolicyDiagnosticsService` and `IPolicyRegistry` provide a uniform way to inspect effective policies.
- **Avoid breaking the public API in this phase:** Consumers should continue using `AddMethodCache(...)`, `[Cache]`, etc., with no immediate legacy removal.

## Task Breakdown

### 1. Source Generator & Decorators ✅ COMPLETED

#### Source Generator Partial-Class Split ✅
- [x] **Baseline the existing generator**
- [x] **Reconcile existing partials**
- [x] **Agree on target file layout**
- [x] **Introduce partial skeletons**
- [x] **Incrementally move implementations**
- [x] **Wire up shared state explicitly**
- [x] **Update generator baselines and tests** - All 17 unit tests + 23 integration tests pass ✅
- [x] **Clean up & document** - Generator organized into partial classes under `Generator/` folders

#### Policy Pipeline Integration ✅
- [x] **Emit policy registrations**: `GeneratedPolicyRegistrations.AddPolicies()` emits `PolicySourceRegistration` with priority 10
  - Implements `GeneratedAttributePolicySource` as `IPolicySource`
  - Each method generates a `PolicySnapshot` from attribute metadata
  - Located in: `MethodCacheGenerator.EmitPolicies.cs`

- [x] **Adjust decorator constructors**: Decorators inject `ICacheManager`, `IPolicyRegistry`, `ICacheKeyGenerator` directly
  - No `IServiceProvider` dependency
  - Stores `_policyRegistry` and `_keyGenerator` as fields
  - Located in: `MethodCacheGenerator.EmitDecorators.cs` lines 105-108, 196-206

- [x] **Runtime policy lookup**: Each cached method resolves policy at runtime
  - Line 238: `var policyResult = _policyRegistry.GetPolicy("{methodId}");`
  - Line 239: `var settings = CachePolicyConversion.ToCacheMethodSettings(policyResult.Policy);`
  - Invalidation methods also use `_policyRegistry` for tag resolution

- [x] **Update generated DI helpers**: `Add{Interface}WithCaching` methods properly wire up policy pipeline
  - Lines 66, 87: Call `GeneratedPolicyRegistrations.AddPolicies(services);`
  - Lines 68-73, 89-94: Construct decorators with `ICacheManager`, `IPolicyRegistry`, `ICacheKeyGenerator`
  - No `MethodCacheConfiguration` references
  - Located in: `MethodCacheGenerator.EmitDI.cs`

- [x] **Tests**: All generator tests pass and verify policy pipeline integration
  - 17/17 unit tests passing ✅
  - 23/23 integration tests passing ✅
  - Generated code compiles against `IPolicyRegistry` successfully
  - Integration harness properly references `MethodCache.Abstractions`/`MethodCache.Core`

**Summary:** The source generator already fully implements the policy pipeline! All decorators use `IPolicyRegistry` for runtime resolution, attributes flow through `GeneratedPolicyRegistrations`, and DI helpers properly register `PolicySourceRegistration`s.

### 2. DI Surface & Configuration APIs ✅ COMPLETED
- [x] **Maintain existing overloads**: `AddMethodCache`, `AddMethodCacheFluent`, and `AddMethodCacheWithSources` stay in place but are internally reimplemented to build policy registrations.
- [x] **Attributes**: Dual-path approach for maximum compatibility:
  - **Primary path**: Generated code uses `GeneratedPolicyRegistrations.AddPolicies()` → policy pipeline (priority 10)
  - **Fallback path**: Runtime attribute scanning via `LoadCacheAttributesIntoConfiguration` for non-generated interfaces (e.g., test-only, nested interfaces)
  - Fallback populates `MethodCacheConfiguration` directly for backward compatibility
- [x] **Fluent/programmatic configuration**: `FluentPolicySource` is registered at lines 31, 64, 96 of `MethodCacheServiceCollectionExtensions.cs` with priority 40.
- [x] **JSON/YAML/Options monitor**: `ConfigurationManagerPolicySource` is registered at line 127-131 of `ConfigurationExtensions.cs` with priority 50.
- [x] **Runtime overrides**: `RuntimeOverridePolicySource` is now registered at line 133-138 of `ConfigurationExtensions.cs` with priority 100 (highest precedence).
- [x] **Policy registration aggregator**: All sources properly registered as `PolicySourceRegistration`s consumed by `PolicyResolver`/`PolicyRegistry`.

**Changes Made:**
- Restored `LoadCacheAttributesIntoConfiguration` as fallback for non-generated interfaces (lines 373-463)
- Removed 3 obsolete `CacheMethodRegistry.Register()` calls
- Added `RuntimeOverridePolicySource` registration in `ConfigurationExtensions.cs`
- Verified all 116 Core tests + 23 integration tests + 17 generator tests pass (156 total) ✅

### 3. Runtime Internals ✅ COMPLETED
- [x] **Keep builders temporarily**: `MethodCacheConfiguration` kept as a thin wrapper - wrapped by `FluentPolicySource` which converts fluent configuration to policy snapshots. Still part of public API for backward compatibility.
- [x] **Ensure policy conversions**: `CachePolicyConversion` and `CachePolicyMapper` provide full round-trip support for all metadata fields:
  - ✅ Duration (lines 21, 22-26 in CachePolicyMapper)
  - ✅ Tags (lines 22, 28-32)
  - ✅ KeyGeneratorType (lines 24, 34-38)
  - ✅ Version (lines 23, 40-44)
  - ✅ IsIdempotent (lines 25, 46-50)
  - ✅ ETag metadata (lines 43-61 in CachePolicyConversion, 151-159 in CachePolicyMapper)
- [x] **Unified access**: Runtime components use `IPolicyRegistry` as the primary API:
  - Decorators call `_policyRegistry.GetPolicy(methodId)` (EmitDecorators.cs:238)
  - `PolicyRegistry` uses `PolicyResolver` internally (PolicyRegistry.cs:16, 36-50)
  - `PolicyDiagnosticsService` provides inspection capabilities
  - `MethodCacheConfiguration` is only used for fluent API input, not runtime lookups

**Verification:**
- All 640 tests pass across entire solution ✅
- 116 Core + 23 Integration + 17 Generator + 484 other tests = 640 total ✅
- Decorators exclusively use `IPolicyRegistry` for runtime resolution
- Policy pipeline is the authoritative source for all configuration

### 4. Samples & Documentation ✅ COMPLETED
- [x] **Sample app**: `MethodCache.SampleApp` already demonstrates the policy pipeline:
  - Uses attributes on `IUserService`, `IProductService`, `IOrderService`, `IReportingService`
  - Demonstrates `PolicyDiagnosticsService` (Program.cs:49-58)
  - Shows all configuration sources: attributes, JSON, YAML, programmatic (Program.cs:18-38)
  - Builds and runs successfully with source generator
- [x] **Guides**: Updated `docs/user-guide/CONFIGURATION_GUIDE.md`:
  - Corrected priority table (Attributes:10, Fluent:40, JSON/YAML:50, Runtime:100)
  - Added explanation of policy pipeline architecture
  - Maintained backward compatibility - `IMethodCacheConfiguration` remains in public API
- [x] **Migration guide**: Created `docs/migration/POLICY_PIPELINE_MIGRATION.md`:
  - Explains before/after architecture
  - **No breaking changes** - all existing code works
  - Shows new `PolicyDiagnosticsService` capabilities
  - Troubleshooting guide for common scenarios
  - Advanced section for internal architecture changes

**Documentation Files:**
- User guide: `docs/user-guide/CONFIGURATION_GUIDE.md` (updated priorities + pipeline explanation)
- Migration: `docs/migration/POLICY_PIPELINE_MIGRATION.md` (NEW - comprehensive migration guide)
- Sample: `MethodCache.SampleApp/Program.cs` (already demonstrates diagnostics)

### 5. Testing & Verification ✅ COMPLETED
- [x] **Unit tests**: All existing tests continue to work with policy pipeline
  - Tests using `IMethodCacheConfiguration` still pass (backward compatibility maintained)
  - Policy pipeline is transparent to existing test code
  - No test updates required - architecture change is internal
- [x] **Integration tests**: Source generator and service registration verified
  - Generator tests: 17/17 passing - outputs compile correctly
  - Integration tests: 23/23 passing - decorators use `IPolicyRegistry`
  - DI registration: Generated `Add{Interface}WithCaching` methods work correctly
- [x] **Functional testing**: Complete end-to-end verification
  - ✅ `dotnet build MethodCache.sln` - **Build successful, 0 warnings, 0 errors**
  - ✅ `dotnet test MethodCache.sln` - **All 640 tests passing**
  - ✅ `dotnet run --project MethodCache.SampleApp` - **Runs successfully, demonstrates PolicyDiagnosticsService**

**Test Results Breakdown:**
- Core: 116/116 ✅
- Generator: 17/17 ✅
- Integration: 23/23 ✅
- Abstractions: 11/11 ✅
- ETags: 16/16 ✅
- HttpCaching: 54/54 + 8/8 integration ✅
- Infrastructure: 56/56 ✅
- OpenTelemetry: 62/62 ✅
- Providers.Memory: 39/39 ✅
- Providers.Redis: 44/44 + 21/21 integration ✅
- Providers.SqlServer: 83/83 + 45/45 integration ✅
- Analyzers: 45/45 ✅
- **Total: 640/640 tests passing** ✅

**Sample App Verification:**
- Shows all 36 configured methods with effective policies
- Demonstrates policy sources: "configuration" (attributes + fluent)
- PolicyDiagnosticsService outputs method IDs, durations, and source contributions
- Successfully handles multi-source configuration precedence

### 6. Cleanup & Release Notes ✅ COMPLETED
- [x] **Legacy API decision**: **KEEP `IMethodCacheConfiguration` - NO breaking changes**
  - Rationale: It's the public fluent configuration API, not legacy
  - `FluentPolicySource` wraps it and feeds the policy pipeline
  - All 640 tests pass without modification
  - Users don't need to know about internal pipeline architecture
  - Maintains backward compatibility and API stability
- [x] **Release notes**: Created comprehensive `docs/RELEASE_NOTES_POLICY_PIPELINE.md`
  - New feature: `PolicyDiagnosticsService` with examples
  - Clarified configuration priorities (10, 40, 50, 100)
  - Migration guide reference (no migration needed!)
  - Bug fixes: Runtime overrides now work correctly
  - Internal architecture changes for contributors
  - Zero breaking changes - all existing code works

**Release Deliverables:**
- Release notes: `docs/RELEASE_NOTES_POLICY_PIPELINE.md` (NEW)
- Migration guide: `docs/migration/POLICY_PIPELINE_MIGRATION.md`
- Updated user guide: `docs/user-guide/CONFIGURATION_GUIDE.md`
- Version: 1.1.0 (suggested)

## 🎉 POLICY PIPELINE MIGRATION: COMPLETE ✅

All 6 sections completed successfully:

### ✅ Section 1: Source Generator & Decorators
- Decorators inject `IPolicyRegistry` + `ICacheKeyGenerator` directly
- Generated code emits `GeneratedPolicyRegistrations.AddPolicies()`
- Runtime policy lookup via `_policyRegistry.GetPolicy(methodId)`
- All 17 generator tests + 23 integration tests passing

### ✅ Section 2: DI Surface & Configuration APIs
- All configuration sources flow through policy pipeline
- Attributes (10) → Fluent (40) → JSON/YAML (50) → Runtime (100)
- Added `RuntimeOverridePolicySource` registration
- Maintained fallback for non-generated interfaces
- All 116 Core tests passing

### ✅ Section 3: Runtime Internals
- `CachePolicyConversion` supports all metadata fields (ETags, KeyGenerator, etc.)
- `IPolicyRegistry` is the primary runtime API
- `MethodCacheConfiguration` kept as thin wrapper for fluent input
- Decorators use policy pipeline exclusively

### ✅ Section 4: Samples & Documentation
- SampleApp demonstrates `PolicyDiagnosticsService`
- Updated configuration guide with correct priorities
- Created comprehensive migration guide
- No breaking changes emphasized

### ✅ Section 5: Testing & Verification
- ✅ Full solution build (0 warnings, 0 errors)
- ✅ All 640 tests passing across entire solution
- ✅ Sample app runs successfully
- ✅ End-to-end verification complete

### ✅ Section 6: Cleanup & Release Notes
- No API removal needed - backward compatibility maintained
- Comprehensive release notes created
- Migration guide available
- Ready for v1.1.0 release

## Summary

The policy pipeline architecture is **fully operational and production-ready**. All configuration sources (attributes, fluent API, JSON/YAML, runtime overrides) now flow through a unified pipeline with consistent priority handling and excellent diagnostics capabilities.

**Key Achievement: Zero breaking changes while modernizing the entire configuration architecture.**

### Metrics
- **Files Modified**: ~15 core files
- **Tests Passing**: 640/640 (100%)
- **Breaking Changes**: 0
- **New Features**: PolicyDiagnosticsService, guaranteed priority enforcement
- **Documentation**: 3 comprehensive guides created/updated







