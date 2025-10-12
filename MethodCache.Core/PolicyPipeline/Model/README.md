# Policy Models

Core data models and mapping utilities for the policy pipeline.

## Key Files

### CachePolicyMapper
- Converts `CacheMethodSettings` → `CachePolicy`
- Tracks which fields were set via `CachePolicyFields` flags
- Used by configuration sources to create policy contributions

### CachePolicyConversion
- Bidirectional conversion utilities
- Handles complex mappings (e.g., TimeSpan ↔ duration strings)
- Type-safe conversions between configuration formats

### PolicySnapshotBuilder
- Creates immutable snapshots of resolved policies
- Used for diagnostics and debugging
- Captures policy state at a point in time

### PolicySourceIds
- Constants defining source identifiers
- Examples: `Attribute`, `FluentApi`, `ConfigFile`, `RuntimeOverride`
- Used for blame tracking and diagnostics

### PolicySourcePriority
- Constants defining priority levels
- 10 (Attribute), 40 (ConfigFile), 50 (FluentApi), 100 (RuntimeOverride)
- Drives conflict resolution in `PolicyResolver`

## Policy Structure

The core `CachePolicy` record is defined in `MethodCache.Abstractions.Policies`. It contains:

- Duration settings
- Key generation configuration
- Tags for invalidation
- Version/concurrency control
- Idempotency requirements
- Layer-specific settings (L1/L2/L3)

Each field can be independently set by different sources.

## Provenance Tracking

`PolicyProvenance` records:
- Which source set each field
- Priority level of that source
- Timestamp of last update

This enables rich diagnostics via `PolicyDiagnosticsService`.
