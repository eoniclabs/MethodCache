# Policy Diagnostics

`PolicyDiagnosticsService` shows the resolved cache policies and how they were assembled from the policy sources. `AddMethodCache()` registers it.

## API

```csharp
var diagnostics = services.GetRequiredService<PolicyDiagnosticsService>();

PolicyDiagnosticsReport report = diagnostics.GetPolicy("MyApp.Services.IUserService.GetUserAsync");
IReadOnlyList<PolicyDiagnosticsReport> all = diagnostics.GetAllPolicies();
IReadOnlyList<PolicyContribution> fromConfig = diagnostics.GetContributions("MyApp.Services.IUserService.GetUserAsync", "configuration-files");
IReadOnlyList<PolicyDiagnosticsReport> overridden = diagnostics.FindBySource("runtime-overrides");
```

A `PolicyDiagnosticsReport` has the `MethodId`, the effective `Policy`, every `PolicyContribution` (source id and the fields it set), the contributions grouped by source (`ContributionsBySource`, `GetContributionsFrom(sourceId)`) and `ResolvedAt`.

Source ids are `attributes`, `startup-fluent`, `configuration-files` and `runtime-overrides`.

## Example: Troubleshooting

**Problem:** A method caches for 10 minutes instead of the expected 5.

```csharp
var report = diagnostics.GetPolicy("MyApp.Services.IUserService.GetUserAsync");
// report.Policy.Duration: 00:10:00

var durationSources = report.Contributions
    .Where(c => c.Fields.HasFlag(CachePolicyFields.Duration))
    .Select(c => c.SourceId);
// "attributes", "runtime-overrides"
```

**Solution:** A runtime override sets the duration and wins over the attribute. Remove it with `IRuntimeCacheConfigurator.RemoveAsync`.

## Notes

- The service reads the already resolved policies from `IPolicyRegistry`; queries do not resolve anything again.
- It is thread-safe and read-only.
