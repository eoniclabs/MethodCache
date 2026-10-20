[← MethodCache documentation](../README.md)

# Configuration

A cached method's policy (duration, sliding expiration, refresh-ahead, fail-safe, tags, group, version) can come from
four layers. Change it in `appsettings.json` or at runtime, and the next call uses it, without a redeploy.

| Layer | How you set it | Priority | Changes while running |
|-------|----------------|----------|-----------------------|
| Attributes | `[Cache(...)]` on the interface method | 10 (lowest) | No |
| Startup code | `AddMethodCache(config => ...)` | 40 | No |
| Configuration | `AddMethodCache(b => b.FromConfiguration(configuration))` | 50 | Yes, when the configuration reloads |
| Runtime overrides | `IRuntimeCacheConfigurator` | 100 (highest) | Yes |

When several layers configure the same method, they are merged setting by setting, and the highest layer that sets a
setting wins. A runtime override of `Duration` leaves the attribute's `Tags` in place.

Tags are the exception: they are combined, not replaced. Tags from startup code, `appsettings.json`, runtime overrides
and the defaults are added to the `[Cache]` attribute's tags (without duplicates), so a `[CacheInvalidate]` that
lists an attribute tag always reaches the entry. To replace the tags instead, call `ClearTags()` before `AddTags`
(`o => o.ClearTags().AddTags("orders")`), or set `"ClearTags": true` next to `Tags` for a method in
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
    config.Defaults(o => o.WithDuration(TimeSpan.FromMinutes(15)));

    // Methods in the "user-data" group: [Cache(Group = "user-data")] or WithGroup("user-data")
    config.Group("user-data", o => o.WithSlidingExpiration(TimeSpan.FromMinutes(10)));

    // One method
    config.Method((IUserService s) => s.GetProfileAsync(Any<int>.Value))
          .Configure(o => o.WithDuration(TimeSpan.FromHours(2)).WithRefreshAhead(TimeSpan.FromMinutes(5)));
});
```
<!-- endSnippet -->

- `Defaults` applies to every cached method, also ones that only have `[Cache]`. `[Cache(Duration = "00:10:00")]`
  keeps its ten minutes while the defaults fill in what it leaves out.
- A method joins a group with `[Cache(Group = "name")]`, `WithGroup("name")` in code, or `Group` in `appsettings.json`.
  Group names are case-sensitive. A group's defaults come from `config.Group(...)` and from `"Groups"` in
  `appsettings.json`, which wins where both set a value; a method's own settings win over both.
- `CacheWhen` is set for one method only. In `Defaults` or a group it fails at startup
  ([Conditional caching](conditional-caching.md)).
- `Any<T>.Value` stands for any argument when you point at a method.

Startup uses `MethodCacheBuilder`: `Defaults`, `Group`, typed `Method` expressions, `Memory`, `Distributed`,
`Telemetry`, `FromConfiguration`, `FromAssemblyOf` and explicit `AutoRegister(lifetime)`. Redis, SQL Server and
MessagePack packages add `UseRedis`, `UseSqlServer` and `UseMessagePack`; Core provides `UseJson`. Use one
distributed cache: `UseRedis` and `UseSqlServer` together throw. `Distributed(...)`, `UseMessagePack` or `UseJson`
without a distributed cache logs a warning, since they have nothing to apply to. `builder.Services` is the service
collection, for a package that adds its own `UseXxx` method.
`Method` on a method without `[Cache]` fails at startup, and on a method that returns nothing (`void`, `Task`,
`ValueTask`) throws an `ArgumentException`.
All integration packages require the exact same Core package version; do not override this bound.
CI uses strict restore (NU1608 as an error) to reject direct-version overrides. Configure services before `AddMethodCache`.
Runtime overrides use the same immutable options builder as startup and manual cache calls.

## appsettings.json

<!-- snippet: configuration-appsettings -->
```cs
builder.Services.AddMethodCache(methodCache => methodCache.FromConfiguration(builder.Configuration));   // the "MethodCache" section
```
<!-- endSnippet -->

```json
{
  "MethodCache": {
    "Defaults": { "Duration": "00:15:00" },
    "FailSafe": { "Duration": "01:00:00" },
    "Groups": {
      "user-data": { "SlidingExpiration": "00:10:00" }
    },
    "Methods": {
      "[MyApp]MyApp.IUserService.GetProfileAsync": {
        "Duration": "01:00:00",
        "SlidingExpiration": "00:10:00",
        "RefreshAhead": "00:05:00",
        "Tags": ["users"],
        "Version": 1,
        "Notes": "Profiles change rarely"
      },
      "[MyApp]MyApp.IAccountService.GetBalanceAsync": { "FailSafe": "off" }
    }
  }
}
```

- Each key under `Methods` is the **method id**: the escaped assembly simple name in brackets, the full interface name, a dot and the method name (for example `[MyApp]MyApp.IUserService.GetProfileAsync`). Assembly versions do not participate. A generic
  interface is written with its type parameters (`[MyApp]MyApp.IRepository<T>.GetAsync`), and an overloaded method adds its
  parameter types (`[MyApp]MyApp.IUserService.FindAsync(int)`). Parameter types other than built-in ones are qualified by
  namespace (`[MyApp]MyApp.IUsers.Find(MyApp.Request)`), so overloads taking `A.Request` and `B.Request` stay separate. Types
  are named as reflection sees them: a tuple is `System.ValueTuple<int, string>` (element names dropped), `nint` is
  `System.IntPtr` and `dynamic` is `object`. Runtime overrides by method id use the same format, and the parameter
  list is also part of the method's cache keys. The admin endpoints list every method id ([Monitoring](monitoring.md)),
  and `IMethodCacheKeys.MethodId<T>(s => s.FindAsync(0))` returns the id of a call ([Cache keys](keys.md)).
- Settings: `Duration`, `SlidingExpiration`, `RefreshAhead` and `FailSafe` (each but `Duration` a time span or `"off"`), `FailSafeTimeout`,
  `FailSafeRetryAfter`, `Tags`, `Version`, `Enabled` and `Distributed`. A group under `Groups` and a method under
  `Methods` can also set `ClearTags` (`true` or `false`): `true` makes their `Tags` replace the lower layers' instead of
  adding to them. Only a method can set `Group`, `Metadata` and `Notes`; in `Defaults` or a group they are an error.
  `Metadata` and `Notes` are recorded for diagnostics and never change caching; to put a method in a group, use
  `Group`. Names are case-insensitive; any other name is an error, so a misspelled `"Durration"` is reported
  instead of ignored. The same goes for the section itself: it holds only `Defaults`, `FailSafe`, `Groups` and
  `Methods`. A leftover `Services` section from 1.0.0-rc.1 is reported as an unknown setting.
- Tag templates such as `"order:{orderId}"` are checked against the parameters of every method they reach, as for
  `[Cache]` ([MC0010](../user-guide/DIAGNOSTICS.md#mc0010)): a template that names a parameter a method doesn't have
  fails at startup.
- Durations use the same invariant grammar as attributes: positive unit values such as `"30s"` or `"1.5h"`,
  or `"hh:mm:ss"` (including `"d.hh:mm:ss"`). A bare number such as `"300"` is rejected rather than interpreted as days. `Tags` is a list (`["users"]`), and a method's `FailSafe` is one time span: the
  `{ "Duration", "Timeout", "RetryAfter" }` block exists only at the top of the section. Setting the same value in
  that block and in `Defaults` (for example `FailSafe.Timeout` and `Defaults.FailSafeTimeout`) is an error.
- A method can also be listed under an index with a `MethodId` property. Environment variable names can't hold `.`,
  `(` or `<`, so this is how environment variables, Kubernetes `env` or Linux App Service configure a method:

  ```bash
  MethodCache__Methods__0__MethodId="[MyApp]MyApp.IUserService.FindAsync(int)"
  MethodCache__Methods__0__Duration="00:02:00"
  ```

  A method can be listed once. Listing it both by name and under an index, for example by name in
  `appsettings.json` and under an index in an environment variable, fails at startup and names both paths. To
  override one file's settings from another source, list the method under the same index in both.
- `Defaults` applies to every cached method; the top-level `FailSafe` section is described in [Fail-safe](fail-safe.md).
- Pass a section name as the second argument to read another section. Environment files such as
  `appsettings.Production.json` work as usual. The section is read when the host starts, so configuration sources
  added after `FromConfiguration`, such as a later `AddJsonFile` or a `WebApplicationFactory`'s test
  settings, apply from the first call.
- Changes apply while the application runs, as soon as the configuration reloads (`appsettings.json` reloads on save
  by default).
- Only methods with `[Cache]` (or listed in `[assembly: CacheInterface]`) have a decorator, so the file can change
  their policy but can't make other methods cached. A method id that matches no cached method, such as a misspelled
  name or an id without its `[Assembly]` prefix, fails at startup and suggests the closest cached id. `*` and
  `group:<name>` are not method ids; use `Defaults` and `Groups`.
- An invalid value, such as a `Duration` that isn't a positive time span, makes the host fail to start with an
  `InvalidOperationException` naming the setting (without a generic host, the first cached call throws). If a
  reloaded file has one, the reload is logged as an error naming the setting's path, never its value, and the
  previous policies stay.

## Runtime overrides

A runtime override of a method wins over that method's other layers (its attribute, startup code and
`appsettings.json`). Use them to change a policy without a deploy, for example to shorten a duration during an
incident:

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

| Method | What it does |
|--------|--------------|
| `UpsertAsync` | Adds or replaces the override for one method, by expression (`UpsertAsync<IOrdersService>(s => s.GetAsync(0), ...)`) or method id |
| `RemoveAsync` / `ClearAsync` | Removes one override, or all of them |
| `GetOverrides()` | Returns the active overrides, ordered by method id, with their options and audit metadata |

The async operations return `ValueTask` and take cancellation last. A method id or expression that reaches no cached
method, or a method that returns nothing, throws an `ArgumentException` naming the closest cached id before
anything is stored. A tag template that doesn't fit the method throws too.
Runtime configuration changes methods; group and global defaults are configured at startup or through configuration files.
Tags remain additive unless `ClearTags()` is used. `WithEnabled(false)` disables caching; `WithDistributed(false)`
disables L2 reads and writes. Re-enabling or changing behavior retires the previous generation.

An effective behavior change immediately retires local cached values and in-flight publication guards.
Already running callers may finish with their earlier result. Audit-only updates and equal effective options retain
values. Overrides are private to the container, use a fresh private L2 namespace when behavior changes, and disappear
on restart. Removing an override restores the current shared declarative namespace after method invalidation.
Invalidation messages retire data on other nodes; they never distribute policy.

`RuntimeOverrideMetadata` retains owner, reason and ticket for the audit trail, and `ExpiresAt` removes the override
when it passes.
Configuration reload publishes local policy and generation changes before its notification returns. Other nodes reload
their own configuration; the library does not distribute files. See [distributed guarantees](distributed-cache.md#policy-isolation).

## Seeing the effective policy

`ICacheAdministrator.GetMethods()` shows each method's effective immutable options, where each setting comes from,
and whether its L2 namespace is shared or private:

<!-- snippet: configuration-diagnostics -->
```cs
var diagnostics = app.Services.GetRequiredService<ICacheAdministrator>();

foreach (var report in diagnostics.GetMethods())
{
    var sources = string.Join(", ", report.SettingSources.Values.Distinct());
    Console.WriteLine($"{report.MethodId}: Duration={report.Options.Duration}, from {sources}");
}
```
<!-- endSnippet -->

`SettingSources` is keyed by the `CacheEntryOptions` property name (`Duration`, `Tags`, `SlidingExpiration`,
`RefreshAhead`, `Version`, `FailSafe`, `FailSafeTimeout`, `FailSafeRetryAfter`, `Enabled`, `Distributed`), and each
value is a `CacheSettingSource`: `Attribute`, `Code`, `Configuration` or `RuntimeOverride`. A setting no layer sets
is left out. The admin endpoint `GET /methodcache/methods` shows the same, with the sources as strings
([Monitoring](monitoring.md)).

## Validation

| Setting | Rule | Checked |
|---------|------|---------|
| `Duration` | A positive time span | `[Cache]`: at build time ([MC0004](../user-guide/DIAGNOSTICS.md#mc0004)). Code: `ArgumentOutOfRangeException`. JSON: at startup |
| `SlidingExpiration`, `RefreshAhead`, `FailSafe` | A positive time span or `"off"`; in code, `TimeSpan.Zero` turns it off | `[Cache]`: at build time ([MC0004](../user-guide/DIAGNOSTICS.md#mc0004)). Code: `ArgumentOutOfRangeException` for a negative value. JSON: at startup |
| `RefreshAhead` | Shorter than `Duration` and `SlidingExpiration` | `[Cache]`: at build time ([MC0023](../user-guide/DIAGNOSTICS.md#mc0023)). Code: `InvalidOperationException` when the options are built. JSON: at startup |
| `FailSafeTimeout`, `FailSafeRetryAfter` | A positive time span | `[Cache]`: at build time. Code (`WithFailSafeTimeout`, `WithFailSafeRetryAfter`): `ArgumentOutOfRangeException`. JSON: at startup |
| `Tags` | Not empty or whitespace; templates name parameters of the method | `[Cache]`: at build time. Code: `AddTags` throws an `ArgumentException` for an empty tag; templates at startup. JSON: at startup |
| `Version` | Zero or more | Code: `ArgumentOutOfRangeException`. JSON: at startup. On `[Cache]`, a negative explicit value is MC0021; omission inherits |

`CacheEntryOptions.Builder` applies to startup, configuration, runtime overrides and manual calls. Each setting has one
`With` method named like the attribute setting, such as `WithFailSafe`, `WithFailSafeTimeout` and
`WithFailSafeRetryAfter`. `CacheWhen<T>` is configured in code for one method; a `CacheWhen<T>` for a type the method
can't return, such as `CacheWhen<string>` on a method that returns `int`, throws an `InvalidOperationException` on the
call instead of silently caching nothing. Cache callbacks and context predicates are not supported.

Overloaded generic methods include generic arity in their policy ids and key prefixes: `Get(int)`, `Get``1(int)` and `Get``2(int)` remain distinct. Generator and runtime configuration use the same ids; earlier keys for overloaded generic methods become cold misses.

Published options and policy tags are owned, read-only snapshots. Mutating a collection supplied by application code cannot change an existing policy or bypass its change notifications.

A keyed `ICacheManager` registration remains independent of the default unkeyed manager. Registering another service key does not suppress `AddMethodCache()` or its default administration services.

`[Cache]` is parameterless and all settings are named. `Enabled` and `Distributed` inherit the policy when omitted; explicitly setting either to `false` overrides a lower layer. An omitted `Version` remains unset; an explicit negative version is an error. `FailSafeRetryAfter` sets the fail-safe retry delay (default 30 seconds). `[assembly: CacheInterface(typeof(...), nameof(...))]` retains its required interface and method arguments and offers the same named settings.
