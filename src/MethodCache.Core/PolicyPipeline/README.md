# Policy Pipeline

The policy pipeline turns cache settings from several sources into one `CachePolicy` per cached method, which drives runtime behavior.

```
Policy sources → Resolution → Generated decorators
  (priority)       (merge)       (PolicyHandle)
```

## Flow

1. **Sources** provide cache settings:
   - Attributes (`[Cache]`), emitted by the source generator
   - Startup code (`services.AddMethodCache(config => ...)`)
   - Configuration such as appsettings.json (`services.AddMethodCacheFromConfiguration(configuration)`)
   - Runtime overrides (`IRuntimeCacheConfigurator`)

2. **Resolution** merges them by priority:
   - Each source has a priority (`PolicySourcePriority`)
   - For each field, the highest-priority source that set it wins
   - Every contribution is recorded, so diagnostics can show where a value came from

3. **Runtime**: decorators read the resolved policy through a `PolicyHandle` on every call, so changes apply while the application runs. `PolicyDiagnosticsService` shows the resolved policies.

## Folder Structure

- **Sources/** - Policy source implementations
- **Model/** - Policy builder, mapping helpers, ids and priorities
- **Resolution/** - Priority-based resolver and registry
- **Diagnostics/** - Runtime policy inspection

## Priorities

- **10** - Attributes (lowest)
- **40** - Startup code (fluent API)
- **50** - Configuration files
- **100** - Runtime overrides (highest)

## Policy Fields

`CachePolicy` has `Duration`, `SlidingExpiration`, `RefreshAhead`, `Tags`, `Version` and `Metadata`. `CachePolicyFields` flags say which fields a source set, so each field can come from a different source.
