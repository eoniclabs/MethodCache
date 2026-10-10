# Policy Models

Data models and helpers for the policy pipeline. The `CachePolicy` record itself is in `MethodCache.Abstractions.Policies`.

## Key Files

### CachePolicyBuilder
- Builds a `CachePolicy` field by field (`WithDuration`, `WithSlidingExpiration`, `WithRefreshAhead`, `WithVersion`, `SetTags`, `AddMetadata`)
- Tracks which fields were set as `CachePolicyFields` flags
- Used by the configuration file source and by `IRuntimeCacheConfigurator.UpsertAsync(methodId, Action<CachePolicyBuilder>)`

### CachePolicyMapper
- `DetectFields` infers which fields a `CachePolicy` sets
- `AttachContribution` records a source's contribution on a policy

### PolicySnapshotBuilder
- Creates the `PolicySnapshot` and `PolicyChange` records that sources publish

### MethodIds
- Builds method ids: the interface's full name, a dot, and the method name

### PolicySourceIds
- Source identifiers: `attributes`, `startup-fluent`, `configuration-files`, `runtime-overrides`

### PolicySourcePriority
- Priorities: `Attributes` (10), `StartupFluent` (40), `ConfigurationFiles` (50), `RuntimeOverrides` (100)

## Policy Structure

`CachePolicy` contains `Duration`, `SlidingExpiration`, `RefreshAhead`, `Tags`, `Version`, `Metadata` and `Provenance`. Each field can be set by a different source.

## Provenance

`PolicyProvenance` is the list of `PolicyContribution`s: which source contributed, which fields it set, and when. `PolicyDiagnosticsService` exposes it.
