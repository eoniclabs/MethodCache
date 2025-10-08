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

### 3. Runtime Internals
- [ ] **Keep builders temporarily**: During the transition, you may keep `MethodCacheConfiguration` as a thin shell that ultimately adds registrations to the pipeline. Once everything has been rerouted, this class can be removed or marked obsolete.
- [ ] **Ensure policy conversions**: If `CachePolicyConversion` lacks a support for certain metadata fields (ETags, key generator, etc.), extend it so decorators receive all relevant settings.
- [ ] **Unified access**: The only runtime API for effects should be `IPolicyRegistry`, `PolicyDiagnosticsService`, and `RuntimeOverridePolicySource`. No other component should need `MethodCacheConfiguration`.

### 4. Samples & Documentation
- [ ] **Sample app**: Update `MethodCache.SampleApp` to rely on the new initialization path (e.g., call `GeneratedPolicyRegistrations.AddPolicies(registrationList)`) and demonstrate `PolicyDiagnosticsService` output.
- [ ] **Guides**: Revise docs to explain that attributes, config files, and programmatic overrides now contribute to the same policy pipeline. Remove `IMethodCacheConfiguration` references from user-facing documentation once the shim is removed.
- [ ] **Migration section**: Provide explicit instructions in docs/release notes on how the new pipeline works and how developers can inspect policies.

### 5. Testing & Verification
- [ ] **Unit tests**: Update any tests referencing `MethodCacheConfiguration` to use the new pipeline (e.g., building a list of `PolicySourceRegistration`s).
- [ ] **Integration tests**: Ensure source generator tests compile the new outputs; service registration tests verify DI while using the new policy registration flow.
- [ ] **Functional testing**: Run `dotnet build MethodCache.sln`, `dotnet test` for all test projects, and sample/demos to ensure the end-to-end flow is intact.

### 6. Cleanup & Release Notes
- [ ] **Legacy removal (optional)**: Once you confirm migration is complete, remove `MethodCacheConfiguration` and related abstractions or mark them `[Obsolete]`.
- [ ] **Release notes**: Document the internal change (policy pipeline) and highlight new diagnostics capabilities. If you remove the legacy API later, include a breaking-changes section.

## Summary
This plan keeps every configuration entry point intact while unifying their execution paths through `PolicyResolver` and `IPolicyRegistry`. The key is to rewrite the generator and DI internals to produce/consume policy registrations, ensuring diagnostics and runtime behavior are consistent regardless of configuration source.







