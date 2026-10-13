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

Tags are the exception: they are combined, not replaced. Tags from startup code, `appsettings.json`, runtime overrides
and the defaults are added to the `[Cache]` attribute's tags (without duplicates), so a `[CacheInvalidate]` that
lists an attribute tag always reaches the entry. To replace the tags instead, call `ClearTags()` before `WithTags`
(`o => o.ClearTags().WithTags("orders")`), or set `"ClearTags": true` next to `Tags` for a method in
`appsettings.json`. The tags of the lower layers and of the defaults are then dropped, and an entry without them is
no longer removed by a `[CacheInvalidate]` that lists them.

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
  parameter types (`MyApp.IUserService.FindAsync(int)`). Parameter types other than built-in ones are qualified by
  namespace (`MyApp.IUsers.Find(MyApp.Request)`), so overloads taking `A.Request` and `B.Request` stay separate. Types
  are named as reflection sees them: a tuple is `System.ValueTuple<int, string>` (element names dropped), `nint` is
  `System.IntPtr` and `dynamic` is `object`. Runtime overrides by method id use the same format, and the parameter
  list is also part of the method's cache keys. The admin endpoints list every method id ([Monitoring](monitoring.md)).
- Settings: `Duration`, `SlidingExpiration`, `RefreshAhead`, `FailSafe` (a time span or `"off"`), `FailSafeTimeout`,
  `FailSafeRetryAfter`, `Tags`, `Version`, `Group`, `Metadata` and `Notes`, plus `ClearTags` (`true` or `false`) for a
  method under `Services`: `true` makes its `Tags` replace the attribute's instead of adding to them. `Metadata` and `Notes` are recorded for
  diagnostics and don't change caching. Names are case-insensitive; any other name is an error, so a misspelled
  `"Durration"` is reported instead of ignored.
- Time spans are written `"hh:mm:ss"` (or `"d.hh:mm:ss"`); a bare number such as `"300"` is rejected, since .NET
  would read it as 300 days. `Tags` is a list (`["users"]`), and a method's `FailSafe` is one time span: the
  `{ "Duration", "Timeout", "RetryAfter" }` block exists only at the top of the section.
- A method can also be listed under an index with a `MethodId` property. Environment variable names can't hold `.`,
  `(` or `<`, so this is how environment variables, Kubernetes `env` or Linux App Service configure a method:

  ```bash
  MethodCache__Services__0__MethodId="MyApp.IUserService.FindAsync(int)"
  MethodCache__Services__0__Duration="00:02:00"
  ```
- `Defaults` applies to every cached method; the top-level `FailSafe` section is described in [Fail-safe](fail-safe.md).
- Pass a section name as the second argument to read another section. Environment files such as
  `appsettings.Production.json` work as usual. The section is read when the host starts, so configuration sources
  added after `AddMethodCacheFromConfiguration`, such as a later `AddJsonFile` or a `WebApplicationFactory`'s test
  settings, apply from the first call.
- Changes apply while the application runs, as soon as the configuration reloads (`appsettings.json` reloads on save
  by default).
- Only methods with `[Cache]` (or listed in `[assembly: CacheInterface]`) have a decorator, so the file can change
  their policy but can't make other methods cached. A method id that matches no cached method, such as a misspelled
  name, is logged as a warning at startup.
- An invalid value, such as a `Duration` that isn't a positive time span, makes the host fail to start with an
  `InvalidOperationException` naming the setting (without a generic host, the first cached call throws). If a
  reloaded file has one, the reload is logged as an error and the previous policies stay.

## Runtime overrides

A runtime override of a method wins over that method's other layers (its attribute, startup code and
`appsettings.json`). Use them to change a policy without a deploy, for example to shorten a duration during an
incident:

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

- Runtime defaults, `DefaultPolicy` or `ForGroup` inside `ApplyAsync`, are defaults like the ones in startup code:
  they fill in what a method leaves unset, and a method's own values, such as `[Cache(Duration = "01:00:00")]`, still
  win over them. To change one method, override that method.
- Tags in an override are added to the method's other tags; call `ClearTags()` first to replace them (see above).
- `RuntimeOverrideMetadata` (owner, reason, ticket, expiry) is kept with the override for auditing. An override stops
  applying when its `ExpiresAt` passes.
- Overrides are held in memory. They are lost on restart and not shared between instances.
- Concurrent changes apply in order: the latest one wins, also for removal, clearing and expiry.
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
| `FailSafe` | A positive time span or `"off"`; in code, `TimeSpan.Zero` turns it off | `[Cache]`: at build time ([MC0004](../user-guide/DIAGNOSTICS.md#mc0004)). Code: `ArgumentOutOfRangeException` for a negative value. JSON: at startup |
| `FailSafeTimeout`, `FailSafeRetryAfter` | A positive time span | `[Cache]` (`FailSafeTimeout` only): at build time. Code: `ArgumentOutOfRangeException`. JSON: at startup |
| `Version` | Zero or more | Code: `ArgumentOutOfRangeException`. JSON: at startup. On `[Cache]`, a negative value means "not set" (the default is -1) |

`OnHit`, `OnMiss`, `WithMetrics` and `When` only apply to [manual caching](manual-caching.md); configuring them for a
`[Cache]` method throws `InvalidOperationException` at startup instead of being silently ignored. "At startup" means
when the generic host starts (`app.Run()`, `host.StartAsync()`), which loads every policy source; with a plain
`BuildServiceProvider()`, the first cached call loads them and throws. To decide per result
whether a `[Cache]` method stores it, use [`CacheWhen`](conditional-caching.md#cachewhen).

Overloaded generic methods include generic arity in their policy ids and key prefixes: `Get(int)`, `Get``1(int)` and `Get``2(int)` remain distinct. Generator and runtime configuration use the same ids; earlier keys for overloaded generic methods become cold misses.

Policy tags are owned, read-only snapshots in both `CachePolicy` and `CacheRuntimePolicy`. Mutating the collection supplied by application code cannot change a published policy or bypass its version/change notifications.

A keyed `ICacheManager` registration remains independent of the default unkeyed manager. Registering another service key does not suppress `AddMethodCache()` or its default administration services.
