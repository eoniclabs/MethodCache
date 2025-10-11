# Policy Pipeline Consolidation Plan

## Context
- MethodCache.Core still routes attribute, fluent, config-file, and runtime override inputs through `CacheMethodSettings` before emitting `CachePolicy` snapshots, leaving two parallel configuration models in play.
- Documentation such as `POLICY_PIPELINE_FINAL_TASKS.md` and `CLEANUP_RECOMMENDATIONS.md` reports that the legacy layer was removed, which no longer matches the current codebase.
- The goal is to preserve all four configuration surfaces while eliminating the `CacheMethodSettings` dependency and the mutable configuration manager that sits alongside the policy pipeline.

## Objectives
1. Make each configuration surface emit `CachePolicy` (plus metadata) directly.
2. Retire `CacheMethodSettings`, `MethodCacheConfiguration`, and related shims without breaking the public fluent API.
3. Align runtime consumption (cache managers, key generators, diagnostics) to rely on policy artifacts only.
4. Bring contributor and user-facing documentation back in sync with the implementation.

## Non-Goals
- No changes to the set of supported configuration surfaces.
- No breaking changes to public DI or attribute APIs beyond deprecating legacy overloads when a compatible alternative exists.
- Storage-layer behavior improvements are out of scope unless required by the policy conversion work.

## Workstreams

### 1. Policy Draft & Builder
- Introduce a lightweight `PolicyDraft` model (method id, `CachePolicy`, `CachePolicyFields`, metadata, notes).
- Add a `CachePolicyBuilder` helper to compose duration, tags, key generator, version, and idempotency without `CacheMethodSettings`.
- Provide utilities to merge defaults/group-level settings directly on the builder.

### 2. Fluent Surface Rewrite
- Update `FluentMethodCacheConfiguration.BuildMethodPolicies()` to return `PolicyDraft` instances.
- Replace `CacheEntryOptionsMapper` with helpers that populate `CachePolicyBuilder`.
- Ensure public interfaces continue to expose the same fluent API while the implementation no longer produces `CacheMethodSettings`.

### 3. Attribute Source Rewrite
- Build policies from `[Cache]` metadata without constructing `CacheMethodSettings`.
- Keep ETag enrichment by emitting metadata directly on the draft.
- Introduce targeted unit tests covering typical attribute combinations and provenance output.

### 4. Config-File Source Rewrite
- Parse configuration sections straight into `CachePolicyBuilder` instances.
- Remove `MergeSettings` cloning logic in favor of builder-level default application.
- Add regression tests for duration, tags, version, and metadata parsing.

### 5. Runtime Overrides
- Extend `IRuntimeCacheConfigurator` to accept either `CachePolicy` or an action against `CachePolicyBuilder`.
- Update `RuntimePolicyStore` to store the enriched policy and fields directly.
- Deprecate `CacheMethodSettings`-based overloads with clear migration guidance.

### 6. Runtime Consumption Updates
- Introduce a slim runtime descriptor (`CacheExecutionDescriptor` or similar) derived from `CachePolicy` for cache managers and key generators.
- Update `ICacheManager`, `ICacheKeyGenerator`, and storage implementations to use the descriptor or `CachePolicy` directly.
- Provide compatibility adapters for downstream packages until public APIs can formally switch.

### 7. Legacy Removal & Cleanup
- Delete `CacheMethodSettings` and its extension helpers once all call sites migrate.
- Remove `MethodCacheConfiguration`, `ManagedMethodCacheConfiguration`, and unused configuration services.
- Prune docs and samples that reference the legacy stack.

### 8. Documentation & Communication
- Refresh `FOLDER_STRUCTURE.md`, runtime configuration docs, and release notes to describe the policy-only architecture.
- Add a migration note summarizing the deprecations and new overloads.
- Ensure the new plan stays discoverable by linking it from `RefactoringRecommendations.md`.

## Milestones
1. **Draft & Fluent Updates Ready** – Fluent surface produces `PolicyDraft` objects; policy builder and tests in place.
2. **Attribute + Config Sources Migrated** – All startup sources emit policies directly; legacy conversion code removed from sources.
3. **Runtime Overrides & Consumption Updated** – Runtime configurator and cache managers operate without `CacheMethodSettings`.
4. **Legacy Types Removed** – Old configuration classes deleted; repository builds successfully; docs refreshed.

## Risks & Mitigations
- **API Compatibility:** introduce `[Obsolete]` warnings with clear alternatives one release ahead of removals.
- **Test Coverage Gaps:** expand unit tests for each policy source and add integration smoke tests to cover the new pipeline.
- **Documentation Drift:** update contributor/user docs in the same PR as code changes to avoid stale guidance.

## Next Actions
1. Publish this plan to the team and confirm scope alignment.
2. Open tracking issues (or project board) for each milestone.
3. Start with the policy builder + fluent rewrite (Workstreams 1 & 2) to unblock the rest of the migration.

## Issue Backlog Outline
Use these as GitHub issues or project board cards to track progress:

1. **PP-001 – Introduce PolicyDraft/CachePolicyBuilder foundation** ✅ _2025-10-12_  
   - Deliverables: new builder types, unit tests, adapters for defaults/groups.
2. **PP-002 – Port fluent configuration to policy builders** ✅ _2025-10-12_  
   - Deliverables: fluent surface producing policy drafts, mapper removal, regression tests.
3. **PP-003 – Rewrite attribute policy source** ✅ _2025-10-12_  
   - Deliverables: direct attribute → policy conversion, ETag metadata coverage, tests.
4. **PP-004 – Rewrite configuration file policy source** ✅ _2025-10-12_  
   - Deliverables: builder-based parser, metadata handling, docs update for JSON examples.
5. **PP-005 – Runtime override & consumption alignment** (in progress)  
   - ✅ Added policy-builder overload to `IRuntimeCacheConfigurator` and `RuntimeCacheConfigurator`  
   - ✅ Introduced `CacheRuntimeDescriptor` and updated source-generated decorators + fluent helpers to consume it  
   - ✅ Fluent `CacheManagerExtensions` emit descriptors alongside legacy settings  
   - ✅ In-memory & hybrid cache managers now consume descriptors/runtime options internally  
   - ◻️ Migrate key generators and runtime helpers (ETag, stampede, distributed locks) off `CacheMethodSettings`
6. **PP-006 – Legacy removal & doc refresh**  
   - Deliverables: delete legacy types, update public docs, finalize migration guidance.

### Final Migration Checklist
Follow these steps to remove the legacy stack entirely once PP-005 groundwork is complete:
1. **Runtime Policy Model:** Introduce a `CacheRuntimePolicy` (or equivalent) derived from `CachePolicy` with runtime-only properties (sliding expiration, refresh ahead, locks, metrics). Update `CacheRuntimeDescriptor` consumers to use it.
2. **Cache Managers:** Update `InMemoryCacheManager`, `HybridCacheManager`, storage coordination, and default/mock managers to operate on runtime policies/descriptors directly; remove `ToCacheMethodSettings()` calls.
3. **Key Generators:** Update `ICacheKeyGenerator` contract and built-in generators to take the new runtime policy, eliminating the `CacheMethodSettings` dependency during key creation.
4. **Helpers & Packages:** Migrate `CacheManagerExtensions`, ETag helpers, stampede protection, distributed locks, and any downstream packages to the runtime policy APIs.
5. **Clean Legacy Types:** Delete `CacheMethodSettings`, `CacheMethodSettingsExtensions`, `CachePolicyConversion`, and `CacheRuntimeDescriptor` shims once all call sites use the new structures.
6. **Documentation & Samples:** Refresh user/developer docs, samples, and migration guides; remove references to the legacy configuration manager or builders.
7. **Final Verification:** Run full build/test suites, grep the repository for `CacheMethodSettings`, and update release notes to announce the removal.
