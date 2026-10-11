# Configuration

A cached method's policy (duration, sliding expiration, refresh-ahead, fail-safe, tags, group, version) can come from
four layers. Change it in `appsettings.json` or at runtime, and the next call uses it, without a redeploy.

| Layer | How you set it | Priority | Changes while running |
|-------|----------------|----------|-----------------------|
| Attributes | `[Cache(...)]` on the interface method | 10 (lowest) | No |
| Startup code | `AddMethodCache(config => ...)` | 40 | No |
| Configuration | `AddMethodCacheFromConfiguration(configuration)` | 50 | Yes, when the configuration reloads |
| Runtime overrides | `IRuntimeCacheConfigurator` | 100 (highest) | Yes |

When several layers configure the same method, they are merged setting by setting, and the highest layer that sets a
setting wins. A runtime override of `Duration` leaves the attribute's `Tags` in place.

## Defaults and groups

Within the layers, each setting comes from the most specific place that sets it: the method itself, then its group,
then the defaults.

<!-- snippet: configuration-startup -->
```cs
builder.Services.AddMethodCache(config =>
{
    // Every cached method: fills in what the method leaves unset
    config.DefaultPolicy(o => o.WithDuration(TimeSpan.FromMinutes(15)));

    // Methods in the "user-data" group: [Cache("user-data")] or WithGroup("user-data")
    config.ForGroup("user-data")
          .Configure(o => o.WithSlidingExpiration(TimeSpan.FromMinutes(10)));

    // One method
    config.ForService<IUserService>()
          .Method(s => s.GetProfileAsync(Any<int>.Value))
          .Configure(o => o.WithDuration(TimeSpan.FromHours(2)).RefreshAhead(TimeSpan.FromMinutes(5)));
});
```
<!-- endSnippet -->

- `DefaultPolicy` applies to every cached method, also ones that only have `[Cache]`. `[Cache(Duration = "00:10:00")]`
  keeps its ten minutes while the defaults fill in what it leaves out.
- A method joins a group with `[Cache("name")]`, `WithGroup("name")` in code, or `Group` in `appsettings.json`.
- `Any<T>.Value` stands for any argument when you point at a method.

## appsettings.json

<!-- snippet: configuration-appsettings -->
```cs
builder.Services.AddMethodCache();
builder.Services.AddMethodCacheFromConfiguration(builder.Configuration);   // the "MethodCache" section
```
<!-- endSnippet -->

```json
{
  "MethodCache": {
    "Defaults": { "Duration": "00:15:00" },
    "FailSafe": { "Duration": "01:00:00" },
    "Services": {
      "MyApp.IUserService.GetProfileAsync": {
        "Duration": "01:00:00",
        "SlidingExpiration": "00:10:00",
        "RefreshAhead": "00:05:00",
        "Tags": ["users"],
        "Version": 1,
        "Notes": "Profiles change rarely"
      },
      "MyApp.IAccountService.GetBalanceAsync": { "FailSafe": "off" }
    }
  }
}
```

- Each key under `Services` is the **method id**: the full name of the interface, a dot and the method name. A generic
  interface is written with its type parameters (`MyApp.IRepository<T>.GetAsync`), and an overloaded method adds its
  parameter types (`MyApp.IUserService.FindAsync(int)`). The admin endpoints list every method id
  ([Monitoring](monitoring.md)).
- Settings: `Duration`, `SlidingExpiration`, `RefreshAhead`, `FailSafe` (a time span or `"off"`), `FailSafeTimeout`,
  `FailSafeRetryAfter`, `Tags`, `Version`, `Group`, `Metadata` and `Notes`. `Metadata` and `Notes` are recorded for
  diagnostics and don't change caching.
- `Defaults` applies to every cached method; the top-level `FailSafe` section is described in [Fail-safe](fail-safe.md).
- Pass a section name as the second argument to read another section. Environment files such as
  `appsettings.Production.json` work as usual.
- Changes apply while the application runs, as soon as the configuration reloads (`appsettings.json` reloads on save
  by default).
- Only methods with `[Cache]` (or listed in `[assembly: CacheInterface]`) have a decorator, so the file can change
  their policy but can't make other methods cached. A method id that matches no cached method, such as a misspelled
  name, is logged as a warning at startup.
- An invalid value, such as a `Duration` that isn't a positive time span, makes `AddMethodCacheFromConfiguration`
  throw at startup. If a reloaded file has one, the reload is logged as an error and the previous policies stay.

## Runtime overrides

Runtime overrides win over everything else. Concurrent mutations carry an ordered revision, so a delayed
notification cannot replace a newer override or remove one that has been renewed. The effective policy agrees
with the latest stored mutation after the operations complete. This also applies to removal, clearing and expiry.

Use runtime overrides to change a policy without a deploy, for example to shorten a
duration during an incident:

<!-- snippet: configuration-runtime-overrides -->
```cs
var configurator = app.Services.GetRequiredService<IRuntimeCacheConfigurator>();

// One method, strongly typed, with who and why for the audit trail
await configurator.UpsertAsync<IOrdersService, Order>(
    s => s.GetAsync(Any<int>.Value),
    o => o.WithDuration(TimeSpan.FromMinutes(2)),
    new RuntimeOverrideMetadata(Owner: "ops", Reason: "Incident 4211", ExpiresAt: DateTimeOffset.UtcNow.AddHours(4)));

// By method id, for example from an admin form
await configurator.UpsertAsync("Docs.Configuration.IUserService.GetProfileAsync",
    o => o.WithDuration(TimeSpan.FromMinutes(10)));

// Back to the lower layers
await configurator.RemoveAsync<IOrdersService, Order>(s => s.GetAsync(Any<int>.Value));
await configurator.ClearAsync();
```
<!-- endSnippet -->

| Method | What it does |
|--------|--------------|
| `UpsertAsync` | Adds or replaces the override for one method, by expression or method id |
| `UpsertBatchAsync` | Several overrides in one call |
| `ApplyAsync` | Overrides for the methods listed with `ForService<T>().Method(...)`, as in startup code |
| `RemoveAsync` / `ClearAsync` | Removes one override, or all of them |
| `GetOverridesAsync` / `GetOverrideAsync` | Returns the active overrides |
| `WatchAsync` | Streams override changes |

- `RuntimeOverrideMetadata` (owner, reason, ticket, expiry) is kept with the override for auditing. An override stops
  applying when its `ExpiresAt` passes.
- Overrides are held in memory. They are lost on restart and not shared between instances.
- An override can't turn caching off: `WithDuration` requires a positive duration. To get rid of stale data,
  [invalidate](invalidation.md) it.

## Seeing the effective policy

`PolicyDiagnosticsService` (in `MethodCache.Policies`) shows each method's merged policy and which layers contributed:

<!-- snippet: configuration-diagnostics -->
```cs
var diagnostics = app.Services.GetRequiredService<PolicyDiagnosticsService>();

foreach (var report in diagnostics.GetAllPolicies())
{
    var sources = string.Join(", ", report.Contributions.Select(c => c.SourceId).Distinct());
    Console.WriteLine($"{report.MethodId}: Duration={report.Policy.Duration}, from {sources}");
}
```
<!-- endSnippet -->

`GetPolicy(methodId)` returns one method's report, and `FindBySource(sourceId)` the methods a layer configured. The
source ids are `attributes`, `startup-fluent`, `configuration-files` and `runtime-overrides`. The admin endpoint
`GET /methodcache/methods` shows the same ([Monitoring](monitoring.md)).

## Validation

| Setting | Rule | Checked |
|---------|------|---------|
| `Duration`, `SlidingExpiration`, `RefreshAhead` | A positive time span | `[Cache]`: at build time ([MC0004](../user-guide/DIAGNOSTICS.md#mc0004)). Code: `ArgumentOutOfRangeException`. JSON: at startup |
| `Version` | Zero or more | Code: `ArgumentOutOfRangeException`. JSON: at startup |

`OnHit`, `OnMiss`, `WithMetrics` and `When` only apply to [manual caching](manual-caching.md); configuring them for a
cached method throws `InvalidOperationException` at startup.

For overloaded methods, non-built-in parameter types are qualified by namespace, for example
`MyApp.IUsers.Find(MyApp.Request)`. This is also the format used by expression-based and runtime configuration.

Generated decorators preserve generic constraints, including `unmanaged`, on cached interfaces and methods.
