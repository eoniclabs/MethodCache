# Runtime Configuration Overview

How cache policies reach a generated decorator, and how they change while the application runs. For usage, see the [configuration page](../features/configuration.md).

## Policy Sources

Each source implements `IPolicySource` and is registered with a priority from `PolicySourcePriority`:

| Priority | Constant | Source | Registered by | Live changes |
|----------|----------|--------|---------------|--------------|
| 100 | `RuntimeOverrides` | `RuntimePolicySource` (`runtime-overrides`) | Always, by `AddMethodCache()` | Yes, through `IRuntimeCacheConfigurator` |
| 50 | `ConfigurationFiles` | `ConfigFilePolicySource` (`configuration-files`) | `AddMethodCache(mc => mc.FromConfiguration(configuration, "MethodCache"))` | Yes, on `IConfiguration` reload |
| 40 | `StartupFluent` | `FluentPolicySource` (`startup-fluent`) | `AddMethodCache(config => ...)` | No |
| 10 | `Attributes` | Policies emitted by the source generator (`attributes`) | Generated module binding | No |

The code is in `src/MethodCache.Core/PolicyPipeline`: `Sources/` (the sources above), `Model/` (`CachePolicyBuilder`, `MethodIds`, `PolicySourceIds`, `PolicySourcePriority`), `Resolution/` (`PolicyResolver`, `PolicyRegistry`) and `Diagnostics/` (`PolicyDiagnosticsService`). The internal `CachePolicy` record is in `Abstractions/Policies`. Public startup configuration enters through `MethodCacheBuilder`; the older root/service/group fluent coordinator interfaces are internal. The method configuration interfaces returned by the builder remain public.

## Resolution

`PolicyResolver` merges the snapshots of all sources for a method field by field. An internal `CachePolicy` has `Enabled`, `Distributed`, `Duration`, `SlidingExpiration`, `RefreshAhead`, `CacheWhen`, `FailSafe`, `FailSafeTimeout`, `FailSafeRetryAfter`, `Tags`, `Version` and `Metadata`, and each source says which of them it set with `CachePolicyFields` flags. For each field, the source with the highest priority that set it wins. Two sources with the same priority, such as two `AddMethodCache(config => ...)` calls, each keep their own layer, and the one registered later wins where both set a field. `Metadata` is merged key by key, with the same order. Every contribution is kept as a `PolicyContribution`, so diagnostics can show where each value came from.

A method id is the simple name of the assembly that declares the interface in brackets, the interface's full name, a dot and the method name, for example `[MyApp]MyApp.IUserService.GetUserAsync`. A generic interface is named by its definition, so one id covers every closed type, and an overloaded method adds its parameter types, for example `[MyApp]MyApp.IUsers.Find(int)` (`MethodIds`). `IMethodCacheKeys.MethodId` returns the id of a call.

### Defaults and groups

Policies can also be registered for two kinds of default ids: `*` for every cached method (`MethodCacheBuilder.Defaults` in startup code, `Defaults` and `FailSafe` in the configuration section) and `group:<name>` for the methods in a group (`MethodCacheBuilder.Group`, and `Groups:<name>` in the configuration section). Neither can be used as a method id. A method is in a group when its resolved `Metadata` has a `group` entry, set by `[Cache(Group = "name")]` or `WithGroup`. Group membership still uses internal metadata; this is not an application-extensible policy source. Each field comes from the method's own layers first, whatever their priority, then from its group's defaults, then from the defaults for every method; within each of these the priority rule above applies. So `[Cache(Duration = "00:10:00")]` (priority 10) beats a default duration in appsettings.json (priority 50). A change to a default re-resolves every method. `CacheWhen` can't be a default: a policy for `*` or a group that sets it fails when the policies load.

`PolicyRegistry` (`IPolicyRegistry`) holds the resolved policy of every method and publishes changes to an internal
`PolicyHandle`. Bound `MethodCache.Generated.CachedMethod` slots receive these updates and publish immutable
invocation snapshots. A decorator reads `Capture()` once; the resulting `MethodCall` owns policy, prefix,
local publication generation, keyed registration and background-scope target. It starts the bound key builder,
probes and loads using that same snapshot. A retired snapshot bypasses caching; publication and retirement use
the same generation guard. Existing singleton decorators receive updates without being recreated.

## Runtime Overrides

`IRuntimeCacheConfigurator` writes to an in-memory store behind `RuntimePolicySource`:

<!-- snippet: configuration-runtime-overrides -->
```cs
var configurator = app.Services.GetRequiredService<IRuntimeCacheConfigurator>();

// One method, strongly typed, with who and why for the audit trail
await configurator.UpsertAsync<IOrdersService>(
    s => s.GetAsync(Any<int>.Value),
    o => o.WithDuration(TimeSpan.FromMinutes(2)),
    new RuntimeOverrideMetadata { Owner = "ops", Reason = "Incident 4211", ExpiresAt = DateTimeOffset.UtcNow.AddHours(4) });

// By method id, for example from an admin form
await configurator.UpsertAsync($"[{typeof(IUserService).Assembly.GetName().Name}]Docs.Configuration.IUserService.GetProfileAsync",
    o => o.WithDuration(TimeSpan.FromMinutes(10)));

// Back to the lower layers
await configurator.RemoveAsync<IOrdersService>(s => s.GetAsync(Any<int>.Value));
await configurator.ClearAsync();
```
<!-- endSnippet -->

Overrides are not persisted and not shared between instances. `GetOverrides()` returns immutable options and separate `RuntimeOverrideMetadata`, ordered by method id. Internally the audit is also carried as `runtime.*` policy/contribution metadata; it is excluded from behavior identity. A timer in `RuntimePolicyStore` removes an override when its `ExpiresAt` passes. Runtime mutation targets methods; group/global defaults are declarative startup/configuration settings.

Effective behavior changes retire local values and in-flight publication guards immediately. Equal effective options and audit-only changes keep them. Declarative policy has a stable shared L2 identity; runtime behavior overrides use a private container namespace. Removing an override restores the current shared declarative identity after method invalidation. Invalidation messages retire data on other nodes and do not distribute policy.

## Configuration Files

`FromConfiguration` parses the section at startup, so invalid values, unknown or repeated method ids and tag templates that don't fit a method throw there. It then follows `IConfiguration` reload tokens: a reload that parses replaces the source's policies, and one that does not is logged with the setting's path (never its value) and ignored, keeping the previous policies.

## Diagnostics

Application code reads effective options, setting sources and the L2 namespace through
`ICacheAdministrator.GetMethods()`. `PolicyDiagnosticsService` and the resolver records are internal
implementation details. Runtime mutation uses `IRuntimeCacheConfigurator` and immutable options; see
[configuration](../features/configuration.md#runtime-overrides).
