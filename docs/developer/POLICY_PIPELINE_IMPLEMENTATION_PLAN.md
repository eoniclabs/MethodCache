# Implementation Plan - Unified Policy Resolver & Configuration Pipeline
## Progress Log
- 2025-10-??: Phase 0 - Preparation completed. Created branch `feature/policy-resolver` and captured baseline `dotnet test MethodCache.sln` run (integration suites for Redis/SQL fail without Docker; recorded as environment limitation). Confirmed toolchain via `global.json` (SDK 8.0.203 roll-forward) and `dotnet --info` (active SDK 9.0.304).
- 2025-10-??: Phase 1 - Introduce Abstractions Project completed. Added MethodCache.Abstractions (multi-target net9.0/netstandard2.0), wired MethodCache.Core/SourceGenerator references, introduced base policy contracts, polyfills, and unit tests (11 passing). Confirmed no existing shared enums required at this stage; will reassess in later phases.
- 2025-10-??: Phase 2 - Implement Policy Sources completed. Added Attribute/Fluent/ConfigFile/RuntimeOverride policy adapters backed by new CachePolicyMapper & snapshot helpers, wired runtime override change notifications, and added core tests covering snapshots and live updates (112 tests passing).
- 2025-10-??: Phase 3 - Policy Resolver & Merge Logic completed. Implemented PolicyResolver with ordered source registrations, layered merge, change streaming, and concurrency controls; added mapping helpers, runtime override notifications, and unit coverage (PolicyResolverTests) validating precedence, live updates, and removals (114 core tests, 11 abstractions tests).
- 2025-10-??: Phase 4 - Policy Registry & DI Integration completed. Wired configuration manager output into the new policy resolver/registry pipeline, added DI helpers, streamed runtime overrides through change notifications, and verified sample app plus core/abstractions test suites (build + tests passing).
- 2025-10-??: Phase 5 - Source Generator & Analyzer Alignment completed. Decorators now resolve policies via IPolicyRegistry/CachePolicyConversion, DI extensions use the new pipeline, tests updated, and generator emits no config-dependent registries.
## 1. Context & Goals
- Preserve MethodCache's multi-surface configuration story while making the effective policy for any method observable, testable, and overrideable at runtime.
- Reduce coupling between configuration parsing (`MethodCache.Core/MethodCacheServiceCollectionExtensions.cs:17`, `MethodCache.Core/Configuration/ConfigurationManager.cs:1`) and execution so new policy sources or cache layers can be added without touching unrelated components.
- Provide a single policy view for tooling, diagnostics, and generated decorators while laying groundwork for future transport layers (Redis, SQL, HTTP caching) to consume the same model.

## 2. Scope & Non-Goals
- **In scope:** new abstractions project, policy source adapters, resolver/registry, DI plumbing changes, generator/analyzer adjustments, tests, docs, migration shims.
- **Out of scope (for now):** redesign of cache storage pipeline, telemetry changes beyond policy provenance, public removal of legacy APIs (they will be marked obsolete and shimmed).

## 3. Target Architecture Overview
- `MethodCache.Abstractions`: holds `CachePolicy`, `CacheCallContext`, `PolicyResolutionResult`, `IPolicySource`, `IPolicyResolver`, `IPolicyRegistry` contracts.
- `MethodCache.Engine` (existing `MethodCache.Core` after refactor): orchestrates pipeline execution using resolved policies rather than direct attribute/config parsing.
- Policy sources feed a `PolicyResolver` service which merges them by precedence (Runtime Overrides -> Startup Fluent/Config -> Attributes -> Defaults) and publishes change notifications.
- A `CachePolicyRegistry` caches resolved policies per method signature and exposes inspection APIs for tooling and generators.
- Decorators/source generator query the registry for effective policies instead of embedding configuration logic.

## 4. Work Breakdown Structure

### Phase 0 - Preparation
- Create feature branch and update solution references.
- Capture baseline tests: `dotnet test MethodCache.sln`.
- Confirm compiler/package versions from `global.json` and `Directory.Build.props`.

### Phase 1 - Introduce Abstractions Project
1. Add new project `MethodCache.Abstractions` (class library targeting current minimum TFM from `Directory.Build.props`).
2. Define foundational contracts:
   - `CachePolicy` record with properties for duration, layers, tags, consistency, key generator, version, metadata, plus provenance map (string source -> property contributions).
   - `CacheCallContext` struct (method id, arguments snapshot, ambient services).
   - `IPolicySource` interface exposing `IAsyncEnumerable<PolicyChange>` where `PolicyChange` contains method id, policy fragment, metadata, and change reason.
   - `PolicyResolutionResult` to include effective policy plus provenance list.
   - `IPolicyResolver` with `Resolve(methodId)` and change subscription API.
   - `IPolicyRegistry` abstraction for caching and inspection.
3. Move any shared enums or delegates from `MethodCache.Core` that are needed by both generator and runtime into abstractions (introduce `InternalsVisibleTo` if required).
4. Update affected projects (`MethodCache.Core`, `MethodCache.SourceGenerator`, tests) to reference the new project.
5. Add unit tests in `MethodCache.Abstractions.Tests` (new project) covering policy merge helpers and provenance metadata.

### Phase 2 - Implement Policy Sources
1. In `MethodCache.Engine` (current core project), create `MethodCache.Core/Configuration/Sources` folder.
2. Implement adapters:
   - `AttributePolicySource`: uses reflection over generated registries to project `[Cache]` and `[CacheInvalidate]` metadata into `CachePolicy` fragments.
   - `FluentPolicySource`: wraps the existing builder API; adjust `FluentMethodCacheConfiguration` to emit policy fragments instead of mutating shared state.
   - `ConfigFilePolicySource`: converts JSON/YAML configuration (existing `MethodCache.Core/Configuration/ConfigurationExtensions.cs`) into policy fragments.
   - `RuntimeOverridePolicySource`: rework `IRuntimeCacheConfigurator` to push updates into this source via change notifications.
3. Each adapter implements `StartAsync(CancellationToken)` if initialization work is needed (e.g., load files once) and exposes `IAsyncEnumerable<PolicyChange>` for incremental updates.
4. Cover adapters with unit tests validating precedence-neutral fragment emission (no merging yet).

### Phase 3 - Policy Resolver & Merge Logic
1. Implement `PolicyResolver` class in `MethodCache.Core/Configuration` that:
   - Accepts ordered `IPolicySource`s via DI registration.
   - Listens to each source's change stream, merges fragments per method using defined precedence.
   - Produces `PolicyResolutionResult` containing the effective `CachePolicy` and provenance data.
   - Raises events or channels for downstream consumers when a policy changes.
2. Provide deterministic merge rules:
   - Higher precedence source always wins for conflicting properties.
   - Sources can clear values (e.g., runtime override removing a tag) via explicit flags in `PolicyChange`.
   - Global defaults apply when no source provides a property.
3. Add concurrency guards (e.g., per-method `ReaderWriterLockSlim`) to protect registry updates.
4. Unit-test the resolver with synthetic sources to ensure correct precedence, clears, and provenance tracking.

### Phase 4 - Policy Registry & DI Integration
1. Build `CachePolicyRegistry` implementing `IPolicyRegistry`:
   - Maintains cache keyed by method id (`MethodSignature` struct or the existing generated keys).
   - Subscribes to `PolicyResolver` change notifications and updates its cache.
   - Exposes read APIs for runtime (`GetPolicy(methodId)`), tooling (`EnumeratePolicies()`), and change events.
2. Replace existing `MethodCacheConfiguration` dependency in `MethodCache.Core/MethodCacheServiceCollectionExtensions.cs:17` with registry-driven lookups:
   - During `AddMethodCache(...)`, register `IPolicySource` instances (attributes/fluent/config/runtime) and the resolver/registry.
   - Ensure backwards compatibility by shimming `IMethodCacheConfiguration` to wrap registry operations (mark obsolete but keep delegating behavior).
3. Update `MethodCache.Core.Tests/Core/ServiceRegistrationTests.cs` to assert new services and compatibility shims.
4. Provide migration notes in `DEVELOPMENT_GUIDE.md` summarizing DI changes.

### Phase 5 - Source Generator & Analyzer Alignment
1. Update `MethodCache.SourceGenerator/MethodCacheGenerator.cs:49` to emit registration manifests containing method ids, default policies, and requirements for providers.
2. Generator should no longer expect `MethodCacheConfiguration` directly; instead, produce code that calls into `IPolicyRegistry` for policy queries.
3. Add analyzer rules ensuring consumers call the new APIs correctly.
4. Expand generator tests to validate manifest emission and DI registration.

### Phase 6 - Diagnostics & Tooling
1. Introduce `PolicyDiagnosticsService` in `MethodCache.Core/Configuration` that can dump effective policy plus provenance; expose via public API and CLI command (if exists in `tools/`).
2. Update documentation (`docs/user-guide/CONFIGURATION_GUIDE.md`, `docs/developer/RUNTIME_CONFIGURATION_OVERVIEW.md`) with new inspection APIs and examples.
3. Provide sample for runtime override diffing in `MethodCache.Demo` or `MethodCache.SampleApp`.

### Phase 7 - Migration & Cleanup
1. Implement shims:
   - `MethodCacheConfiguration` becomes thin adapter around `IPolicyRegistry` for legacy APIs; mark obsolete with guidance.
   - Provide extension methods to convert old fluent API usage to new pattern.
2. Remove redundant configuration paths (e.g., manual attribute scanning) once shims cover scenarios.
3. Update README badges or docs referencing old APIs.
4. Run full test suite and fix regressions.
5. Document breaking changes (if any) in `RELEASE.md` and craft upgrade guide in `docs/migration/METHODCACHE_VNEXT.md`.

## 5. Testing Strategy
- **Unit tests:** abstractions merge helpers, each policy source, resolver precedence, registry caching, shims.
- **Integration tests:** existing suites (`MethodCache.SourceGenerator.Tests`, `MethodCache.Core.Tests`, provider integration tests) updated to target new APIs.
- **Performance benchmarks:** run `MethodCache.Benchmarks` before/after to ensure negligible overhead from registry lookups; add new benchmark for resolver throughput.
- **Smoke tests:** execute sample apps to confirm DI registration and runtime overrides still work.

## 6. Risks & Mitigations
- **Risk:** deadlock or missed updates when multiple sources push changes. *Mitigation:* design resolver with non-blocking channels and exhaustive concurrency tests.
- **Risk:** generator/runtime drift. *Mitigation:* share abstractions via new project, add integration tests that compile generated code against the runtime.
- **Risk:** user friction due to API changes. *Mitigation:* keep shims, mark obsolete, ship migration guide, and add logging when legacy APIs are used.

## 7. Acceptance Criteria
- All configuration inputs flow through `IPolicySource` implementations and the resolver.
- Runtime caches fetch policies only through `IPolicyRegistry`.
- Effective policy for any method can be enumerated with provenance data at runtime.
- Legacy `IMethodCacheConfiguration` scenarios continue to work with deprecation warnings.
- Test suite and benchmarks pass without significant regressions.
- Documentation reflects new architecture and migration steps.
