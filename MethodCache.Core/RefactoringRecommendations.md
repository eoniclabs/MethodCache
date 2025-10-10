# MethodCache.Core Simplification Notes

## Current Pain Points

- **Dual configuration stacks** – `ConfigurationManager` still merges `IConfigurationSource` instances into mutable `CacheMethodSettings`, while the policy pipeline resolves `CachePolicy` snapshots. Every surface (attributes, files, fluent, runtime) flows through both layers before a policy is produced.
- **Overloaded `MethodCacheConfiguration`** – the type is created in DI extensions, mutated during attribute scanning, reused by `FluentPolicySource`, and finally registered as the singleton `IMethodCacheConfiguration`. The same object acts as builder, source snapshot, and runtime state bag, complicating lifetimes.
- **`ManagedMethodCacheConfiguration` cloning** – `ConfigurationExtensions.AddMethodCacheWithSources` reconstructs the legacy manager, then wraps it back into a `MethodCacheConfiguration` clone that replays the entire cache on every change, doubling in-memory state.
- **Runtime override translation chain** – `RuntimeCacheConfigurator` converts fluent builders into `MethodCacheConfiguration`, into `MethodCacheConfigEntry`, into overrides, then reloads the manager so the policy resolver can translate them again.
- **Config file sources as adapters** – `ConfigFilePolicySource` just iterates the legacy `IConfigurationSource` outputs and re-emits them as policies, so every new setting requires changes across multiple layers.

## Recommended Direction

1. Standardise each configuration surface on `IPolicySource`. Let attributes, JSON/YAML, fluent builders, and runtime overrides emit `CachePolicy`/`PolicyContribution` directly and keep a thin compatibility wrapper for legacy APIs that need `CacheMethodSettings`.
2. Replace the mutable `MethodCacheConfiguration` singleton with a pure builder/descriptor that exists only long enough to publish policy registrations. Consumers should interact with read-only policy views, making `ManagedMethodCacheConfiguration` unnecessary.
3. Collapse the overload-heavy DI extensions so they compose `PolicySourceRegistration` instances. Move attribute scanning and generator fallbacks into dedicated helper services to avoid repeated reflection in registration paths.
4. Bridge runtime overrides straight into the policy pipeline (e.g., an `IPolicyOverrideService`) so `RuntimeCacheConfigurator` can emit policy deltas without reconstructing full configuration manager snapshots.
5. Reimplement config file and options-monitor handling as native `IPolicySource` providers that emit snapshots and deltas themselves, eliminating duplicate reload logic and keeping change propagation inside the policy pipeline.

# MethodCache.Abstractions Simplification Notes

## Current Pain Points

- **Unused policy breadth** – `CachePolicy` still exposes layers, consistency, and write strategy knobs that nothing sets or reads (`MethodCache.Abstractions/Policies/CachePolicy.cs:12`–`MethodCache.Abstractions/Policies/CachePolicy.cs:19`, `MethodCache.Abstractions/Policies/CacheLayerSettings.cs:3`). This inflates the public surface without delivering value.
- **Dead enums and kinds** – `CacheConsistencyMode`, `CacheWriteStrategy`, and the extra `PolicyContributionKind` values never appear outside the abstractions assembly (`MethodCache.Abstractions/Policies/CacheConsistencyMode.cs:3`, `MethodCache.Abstractions/Policies/CacheWriteStrategy.cs:3`, `MethodCache.Abstractions/Policies/PolicyContributionKind.cs:3`). Consumers will assume support for behaviours that do not exist.
- **Stray contracts** – `CacheCallContext` is published but unused anywhere in Core or samples (`MethodCache.Abstractions/Context/CacheCallContext.cs:7`). Shipping it implies API stability for scenarios we have not actually implemented.
- **Allocation-heavy provenance** – `PolicyProvenance.Append` clones the contribution list on every call via `Concat().ToArray()` (`MethodCache.Abstractions/Policies/PolicyProvenance.cs:25`). The resolver calls this for every change, so the overhead shows up in hot paths.

## Recommended Direction

1. Trim `CachePolicy` to the fields the pipeline actively resolves (duration, tags, key generator, version, metadata, idempotency). Move dormant concepts like layers/consistency into a design document until there is a real implementation path.
2. Deprecate or internalise unused enums and contribution kinds so the public API reflects the features we actually ship. If we need future-proofing, hide them behind experimental namespaces.
3. Either implement the `CacheCallContext` story end-to-end or remove it from abstractions so downstream users are not locked into an unused ABI.
4. Replace the `PolicyProvenance.Append` cloning with an incremental structure (e.g., pooled builder or simple `List<PolicyContribution>`) to avoid per-change allocations.
