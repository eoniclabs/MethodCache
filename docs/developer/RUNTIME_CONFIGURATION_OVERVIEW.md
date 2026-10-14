# Runtime Configuration Overview

How cache policies reach a generated decorator, and how they change while the application runs. For usage, see the [configuration page](../features/configuration.md).

## Policy Sources

Each source implements `IPolicySource` and is registered with a priority from `PolicySourcePriority`:

| Priority | Constant | Source | Registered by | Live changes |
|----------|----------|--------|---------------|--------------|
| 100 | `RuntimeOverrides` | `RuntimePolicySource` (`runtime-overrides`) | Always, by `AddMethodCache()` | Yes, through `IRuntimeCacheConfigurator` |
| 50 | `ConfigurationFiles` | `ConfigFilePolicySource` (`configuration-files`) | `AddMethodCacheFromConfiguration(configuration, "MethodCache")` | Yes, on `IConfiguration` reload |
| 40 | `StartupFluent` | `FluentPolicySource` (`startup-fluent`) | `AddMethodCache(config => ...)` | No |
| 10 | `Attributes` | Policies emitted by the source generator (`attributes`) | Each generated decorator | No |

The code is in `src/MethodCache.Core/PolicyPipeline`: `Sources/` (the sources above), `Model/` (`CachePolicyBuilder`, `MethodIds`, `PolicySourceIds`, `PolicySourcePriority`), `Resolution/` (`PolicyResolver`, `PolicyRegistry`) and `Diagnostics/` (`PolicyDiagnosticsService`). The `CachePolicy` record is in `Abstractions/Policies`.

## Resolution

`PolicyResolver` merges the snapshots of all sources for a method field by field. A `CachePolicy` has `Duration`, `SlidingExpiration`, `RefreshAhead`, `CacheWhen`, `FailSafe`, `FailSafeTimeout`, `FailSafeRetryAfter`, `Tags`, `Version` and `Metadata`, and each source says which of them it set with `CachePolicyFields` flags. For each field, the source with the highest priority that set it wins. Two sources with the same priority, such as two `AddMethodCache(config => ...)` calls, each keep their own layer, and the one registered later wins where both set a field. `Metadata` is merged key by key, with the same order. Every contribution is kept as a `PolicyContribution`, so diagnostics can show where each value came from.

A method id is the full name of the interface that declares the method, a dot and the method name, for example `MyApp.IUserService.GetUserAsync`. A generic interface is named by its definition, so one id covers every closed type, and an overloaded method adds its parameter types, for example `MyApp.IUsers.Find(int)` (`MethodIds`).

### Defaults and groups

Policies can also be registered for two kinds of default ids: `*` for every cached method (`DefaultPolicy` in startup code, `Defaults` and `FailSafe` in the configuration section) and `group:<name>` for the methods in a group (`ForGroup`). A method is in a group when its resolved `Metadata` has a `group` entry, set by `[Cache("name")]` or `WithGroup`. Each field comes from the method's own layers first, whatever their priority, then from its group's defaults, then from the defaults for every method; within each of these the priority rule above applies. So `[Cache(Duration = "00:10:00")]` (priority 10) beats a default duration in appsettings.json (priority 50). A change to a default re-resolves every method.

`PolicyRegistry` (`IPolicyRegistry`) holds the resolved policy of every method and publishes changes to an internal
`PolicyHandle`. Bound `MethodCache.Generated.CachedMethod` slots receive these updates and publish immutable
invocation snapshots. A decorator reads `Capture()` once; the resulting `MethodCall` owns policy, prefix,
local publication generation, keyed registration and background-scope target. It starts the bound key builder,
probes and loads using that same snapshot. A retired snapshot bypasses caching; publication and retirement use
the same generation guard. Existing singleton decorators receive updates without being recreated.

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
