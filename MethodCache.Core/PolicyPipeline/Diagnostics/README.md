# Policy Diagnostics

Runtime inspection and debugging tools for the policy pipeline.

## PolicyDiagnosticsService

Service for inspecting resolved cache policies at runtime. Provides visibility into how policies were assembled from multiple sources.

### Features

#### 1. Policy Inspection
Get the fully resolved policy for any method:

```csharp
var policy = await diagnostics.GetResolvedPolicyAsync("MyClass.MyMethod");
```

Returns the merged `CachePolicy` actually used by the cache.

#### 2. Provenance Tracking
See which source set each field:

```csharp
var provenance = await diagnostics.GetPolicyProvenanceAsync("MyClass.MyMethod");
// Shows: Duration set by ConfigFile (priority 40)
//        Tags set by FluentApi (priority 50)
```

#### 3. Source Contributions
View individual contributions from each source before merging:

```csharp
var contributions = await diagnostics.GetSourceContributionsAsync("MyClass.MyMethod");
// Returns: List of (Source, Priority, Policy) tuples
```

#### 4. Conflict Detection
Identify when multiple sources configure the same field:

```csharp
var conflicts = await diagnostics.DetectConflictsAsync("MyClass.MyMethod");
// Shows fields where multiple sources disagreed
```

## Use Cases

### Debugging Configuration
When cache behavior is unexpected, use diagnostics to:
1. Check which policy is actually applied
2. Identify which source "won" for each field
3. Detect unintended overrides

### Runtime Monitoring
Expose diagnostics via health checks or admin endpoints:
- Policy audit trails
- Configuration validation
- Runtime override tracking

### Development Tools
Build developer tools on top of diagnostics:
- Configuration diff viewers
- Policy simulation ("what if" scenarios)
- Source impact analysis

## Example: Troubleshooting

**Problem:** Method caches for 10 minutes instead of expected 5 minutes.

**Investigation:**
```csharp
var policy = await diagnostics.GetResolvedPolicyAsync("GetUser");
// Duration: 600 seconds

var provenance = await diagnostics.GetPolicyProvenanceAsync("GetUser");
// Duration: Set by RuntimeOverride (priority 100)
```

**Solution:** A runtime override is setting duration. Check runtime configuration or feature flags.

## Thread Safety

`PolicyDiagnosticsService` is thread-safe and read-only. Safe to call from multiple threads concurrently.

## Performance Impact

Minimal - diagnostics read from already-resolved policies in the registry. No additional resolution occurs during diagnostic queries.
