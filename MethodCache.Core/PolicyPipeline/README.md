# Policy Pipeline

The Policy Pipeline is the core configuration architecture of MethodCache. It transforms configuration from multiple sources into unified `CachePolicy` objects that drive runtime behavior.

## Architecture Overview

```
Configuration Sources → Policy Resolution → Runtime Execution
     (Priority)              (Merge)            (Apply)
```

### Flow

1. **Sources** - Multiple configuration surfaces provide cache settings:
   - Attributes (`[MethodCache]`)
   - Fluent API (`services.ConfigureMethodCache()`)
   - Config Files (JSON/YAML)
   - Runtime Overrides (dynamic changes)

2. **Resolution** - Priority-based merging into unified policies:
   - Each source has a priority (10, 40, 50, 100)
   - Higher priority wins for conflicting settings
   - Blame tracking records which source set each field

3. **Runtime** - Resolved policies drive cache behavior:
   - Duration, key generation, tags, etc.
   - Diagnostics available via `PolicyDiagnosticsService`

## Folder Structure

- **Sources/** - Configuration source implementations
- **Model/** - Policy models, mappers, and metadata
- **Resolution/** - Priority-based policy resolver
- **Diagnostics/** - Runtime policy inspection tools

## Key Concepts

### Priority Hierarchy

- **10** - Attributes (lowest, defaults)
- **40** - Config Files (application-wide settings)
- **50** - Fluent API (programmatic overrides)
- **100** - Runtime Overrides (highest, dynamic changes)

### Policy Fields

Each `CachePolicy` field can be set independently by different sources. The resolver merges them based on priority, creating a composite policy.

### Blame Tracking

`PolicyProvenance` tracks which source set each field, enabling diagnostics and troubleshooting.

## Usage

Developers typically interact with **Sources**, not directly with resolution or models. The pipeline automatically merges configurations.

See individual folder READMEs for detailed documentation.
