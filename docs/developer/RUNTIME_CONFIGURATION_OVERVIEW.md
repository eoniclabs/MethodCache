# Runtime Configuration Overview

How cache policies reach a generated decorator, and how they change while the application runs. For usage, see the [Configuration Guide](../user-guide/CONFIGURATION_GUIDE.md).

## Policy Sources

Each source implements `IPolicySource` and is registered with a priority from `PolicySourcePriority`:

| Priority | Constant | Source | Registered by | Live changes |
|----------|----------|--------|---------------|--------------|
| 100 | `RuntimeOverrides` | `RuntimePolicySource` (`runtime-overrides`) | Always, by `AddMethodCache()` | Yes, through `IRuntimeCacheConfigurator` |
| 50 | `ConfigurationFiles` | `ConfigFilePolicySource` (`configuration-files`) | `AddMethodCacheFromConfiguration(configuration, "MethodCache")` | Yes, on `IConfiguration` reload |
| 40 | `StartupFluent` | `FluentPolicySource` (`startup-fluent`) | `AddMethodCache(config => ...)` | No |
| 10 | `Attributes` | Policies emitted by the source generator (`attributes`) | Each generated decorator | No |

## Resolution

`PolicyResolver` merges the snapshots of all sources for a method field by field (`CachePolicyFields`): for each field, the source with the highest priority that set it wins. Every contribution is kept as a `PolicyContribution`, so diagnostics can show where each value came from.

`PolicyRegistry` (`IPolicyRegistry`) holds the resolved policy of every method and publishes changes to a `PolicyHandle` per method. Every decorator for the method shares that handle and reads `PolicyHandle.Current` on each call, with one volatile read and no lock. A change replaces the whole policy, so a call never sees half of an update, and it reaches services that already exist, including singletons.

## Runtime Overrides

`IRuntimeCacheConfigurator` writes to an in-memory store behind `RuntimePolicySource`:

```csharp
var configurator = services.GetRequiredService<IRuntimeCacheConfigurator>();

await configurator.UpsertAsync<IUserService, UserProfile>(
    s => s.GetUserProfileAsync(default),
    o => o.WithDuration(TimeSpan.FromMinutes(5)),
    new RuntimeOverrideMetadata(Owner: "ops", Reason: "Incident 4211"));

await configurator.RemoveAsync<IUserService, UserProfile>(s => s.GetUserProfileAsync(default));
```

Overrides are not persisted and not shared between instances. `RuntimeOverrideMetadata` is stored as `runtime.*` metadata on the policy; a timer in `RuntimePolicyStore` removes an override when its `ExpiresAt` passes.

## Configuration Files

`AddMethodCacheFromConfiguration` parses the section at startup, so invalid values throw there. It then follows `IConfiguration` reload tokens: a reload that parses replaces the source's policies, and one that does not is logged and ignored, keeping the previous policies.

## Diagnostics

`PolicyDiagnosticsService` reads the registry:

```csharp
var diagnostics = services.GetRequiredService<PolicyDiagnosticsService>();

foreach (var report in diagnostics.GetAllPolicies())
{
    var sources = string.Join(", ", report.Contributions.Select(c => c.SourceId).Distinct());
    Console.WriteLine($"{report.MethodId}: Duration={report.Policy.Duration}, Sources={sources}");
}
```

`GetPolicy(methodId)`, `GetContributions(methodId, sourceId)` and `FindBySource(sourceId)` narrow the view. `ICacheAdministrator.GetMethods()` (and `GET /methodcache/methods` from `MethodCache.AspNetCore`) returns the effective policy of every cached method with its sources.
