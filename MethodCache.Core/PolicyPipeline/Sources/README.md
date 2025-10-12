# Policy Sources

Configuration sources that feed into the policy pipeline. Each source provides cache settings with a specific priority level.

## Available Sources

### AttributePolicySource (Priority 10)
- Reads `[MethodCache]` attributes from methods
- Lowest priority - provides defaults
- Example: `[MethodCache(Duration = 300)]`

### ConfigFilePolicySource (Priority 40)
- Reads from JSON/YAML configuration files
- Application-wide settings
- Example: `appsettings.json` → `MethodCache` section

### FluentPolicySource (Priority 50)
- Programmatic configuration via fluent API
- Explicit overrides in code
- Example: `services.ConfigureMethodCache(cfg => cfg.WithDuration(300))`

### ConfigurationManagerPolicySource
- Integrates with `ConfigurationManager` for policy resolution
- Coordinates multiple sources during resolution

### RuntimeOverridePolicySource (Priority 100)
- Highest priority - runtime dynamic changes
- Allows changing policies without redeployment
- Example: Feature flags, A/B testing

## Priority Rules

When multiple sources configure the same method, the **highest priority wins** for each field:

1. Runtime Override (100) → Always wins
2. Fluent API (50) → Overrides files and attributes
3. Config Files (40) → Overrides attributes
4. Attributes (10) → Baseline defaults

## Adding Custom Sources

Implement `IPolicySource` interface:

```csharp
public interface IPolicySource
{
    int Priority { get; }
    Task<PolicyContribution?> GetPolicyAsync(string methodKey);
}
```

Register via `PolicyRegistrationExtensions` in the Resolution folder.
