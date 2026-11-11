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

## What each surface can set

The four surfaces take the same settings, with the same names, and reject the same mistakes. Where they differ, the
table says why.

| Setting | `[Cache]` | Startup code | `appsettings.json` | Runtime override |
|---|---|---|---|---|
| `Duration`, `SlidingExpiration`, `RefreshAhead`, `FailSafe`, `FailSafeTimeout`, `FailSafeRetryAfter`, `Tags`, `Version`, `Enabled`, `Distributed` | Yes | Yes | Yes | Yes |
| `ClearTags` | No (1) | Yes | Yes | Yes |
| `Group` (which group a method is in) | Yes | Yes | Yes | No (2) |
| `CacheWhen`, `DurationFor`, `SlidingFor`, `TagsFor` | No (3) | Yes | No (3) | Only without a backplane (3) |
| `InvalidatedElsewhere`, `InvalidatedManually` | Yes (4) | No | No | No |
| For every method (`Defaults`) | No | Yes | Yes | Yes, `UpsertAllAsync` (5) |
| For a group | No | Yes | Yes | Yes, `UpsertGroupAsync` (5) |
| Over HTTP | No | No | No | Yes, `PUT /methodcache/overrides` ([Monitoring](monitoring.md#admin-endpoints)) |
| Memory, distributed cache, telemetry | No | Yes (6) | No (6) | No |

1. The attribute is the lowest layer, so it has no tags under it to clear. `mc.Defaults(...)` and `"Defaults"` can't
   clear tags either, for the same reason, and say so at startup.
2. A method's group decides which group settings it gets, which belongs to how the application is built rather than
   to an incident.
3. These are functions. An attribute and a JSON file can't hold one, and another instance can't run one, so an
   override that every instance applies can't carry one.
4. These only tell the analyzers at build time where a method is invalidated; they change no policy.
5. A runtime override always wins, also over a method's own `[Cache]`. So the group and all-methods versions put the
   same override on each method instead of filling in what methods leave unset, as `Defaults` does.
6. These register services rather than set a policy. To read them from configuration, bind a section yourself:
   `mc.Memory(o => builder.Configuration.GetSection("MethodCacheMemory").Bind(o))`.

`WithName` names an application's own `GetOrCreateAsync` call in metrics and logs. A cached method is always named
by its method id, so `WithName` in `mc.Method`, `mc.Defaults`, `mc.Group` or a runtime override is an error.

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

- `Defaults` applies to every cached method, also ones that only have `[Cache]`. `[Cache(Duration = "10m")]`
  keeps its ten minutes while the defaults fill in what it leaves out. `ClearTags()` in `Defaults` fails at startup,
  since nothing under the defaults has tags.
- A method joins a group with `[Cache(Group = "name")]`, `WithGroup("name")` in code, or `Group` in `appsettings.json`.
  Group names are case-sensitive. A group's defaults come from `config.Group(...)` and from `"Groups"` in
  `appsettings.json`, which wins where both set a value; a method's own settings win over both. `ClearTags()` in a
  group drops the tags from `Defaults`, not the method's own tags, which are above it. A group that has settings but
  no cached method in it, usually a typo or a difference in case, is logged as a warning at startup with the closest
  group name in use.
- `CacheWhen`, `DurationFor`, `SlidingFor` and `TagsFor` are set for one method only. In `Defaults` or a group they
  fail at startup ([Conditional caching](conditional-caching.md),
  [A duration from the result](expiration.md#a-duration-from-the-result),
  [A sliding window from the result](expiration.md#a-sliding-window-from-the-result),
  [Tags from the result](invalidation.md#tags-from-the-result)).
- `Any<T>.Value` stands for any argument when you point at a method.

Startup uses `MethodCacheBuilder`: `Defaults`, `Group`, typed `Method` expressions, `Memory`, `Distributed`,
`Telemetry`, `FromConfiguration`, `FromAssemblyOf` and explicit `AutoRegister(lifetime)`. Redis, SQL Server,
PostgreSQL and MessagePack packages add `UseRedis`, `UseSqlServer`, `UsePostgreSql` and `UseMessagePack`; Core provides
`UseJson`. Use one distributed cache: calling more than one of `UseRedis`, `UseSqlServer` and `UsePostgreSql` throws. `Distributed(...)`, `UseMessagePack` or `UseJson`
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
    "Defaults": { "Duration": "15m" },
    "FailSafe": { "Duration": "1h" },
    "Groups": {
      "user-data": { "SlidingExpiration": "10m" }
    },
    "Methods": {
      "[MyApp]MyApp.IUserService.GetProfileAsync": {
        "Duration": "1h",
        "SlidingExpiration": "10m",
        "RefreshAhead": "5m",
        // Profiles change rarely
        "Tags": ["users"],
        "Version": 1
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
  and `IMethodCacheKeys.MethodId<T>(s => s.FindAsync(Any<int>.Value))` returns the id of a call ([Cache keys](keys.md)).
- Settings: `Duration`, `SlidingExpiration`, `RefreshAhead` and `FailSafe` (each but `Duration` a time span or `"off"`), `FailSafeTimeout`,
  `FailSafeRetryAfter`, `Tags`, `Version`, `Enabled` and `Distributed`. A group under `Groups` and a method under
  `Methods` can also set `ClearTags` (`true` or `false`): `true` makes their `Tags` replace the lower layers' instead of
  adding to them. Only a method can set `Group`; in `Defaults` or a group it is an error. Keep notes about a method
  in a `//` comment next to its entry: `Notes` and `Metadata` are errors. Names are case-insensitive; any other name
  is an error, so a misspelled `"Durration"` is reported instead of ignored. A setting written without a value, such
  as `"Enabled": ""`, or an empty tag is an error too, rather than read as unset. The same goes for the section itself: it holds only `Defaults`, `FailSafe`, `Groups` and
  `Methods`. A leftover `Services` section from 1.0.0-rc.1 is reported as an unknown setting.
- Tag templates such as `"order:{orderId}"` are checked against the parameters of every method they reach, as for
  `[Cache]` ([MC0010](../user-guide/DIAGNOSTICS.md#mc0010)): a template that names a parameter a method doesn't have
  fails at startup.
- Durations are written as in attributes, with a unit: `"30s"`, `"5m"`, `"1.5h"`, `"7d"`; see
  [Writing a duration](expiration.md#writing-a-duration). A bare number such as `"300"` is rejected rather than
  interpreted as days, and a `TimeSpan` such as `"00:05:00"` is rejected with the value to write instead (`"5m"`).
- `Tags` is a list (`["users"]`), and a method's `FailSafe` is one duration: the
  `{ "Duration", "Timeout", "RetryAfter" }` block exists only at the top of the section. Setting the same value in
  that block and in `Defaults` (for example `FailSafe.Timeout` and `Defaults.FailSafeTimeout`) is an error.
- A method can also be listed under an index with a `MethodId` property. Environment variable names can't hold `.`,
  `(` or `<`, so this is how environment variables, Kubernetes `env` or Linux App Service configure a method:

  ```bash
  MethodCache__Methods__0__MethodId="[MyApp]MyApp.IUserService.FindAsync(int)"
  MethodCache__Methods__0__Duration="2m"
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
- A setting for a `[Cache]` method whose service this application doesn't register is skipped, in the file and in
  `mc.Method(...)`, so configuration and startup code shared by several applications works in each of them. Each
  skipped method is logged once at startup, at Information, so a service you forgot to register shows up in the log.
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
| `UpsertGroupAsync` | Puts the same override on every cached method in a group |
| `UpsertAllAsync` | Puts the same override on every cached method |
| `RemoveAsync` / `RemoveGroupAsync` / `ClearAsync` | Removes one method's override, the overrides of a group's methods, or all of them |
| `GetOverrides()` | Returns the active overrides, ordered by method id, with their options and audit metadata |

To change a whole group or every method at once, for example to turn caching off for the `user-data` group during an
incident:

<!-- snippet: configuration-runtime-groups -->
```cs
// Every method in the "user-data" group gets the same override
await configurator.UpsertGroupAsync("user-data", o => o.WithEnabled(false),
    new RuntimeOverrideMetadata { Reason = "Incident 4211", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });

// Every cached method
await configurator.UpsertAllAsync(o => o.WithDuration(TimeSpan.FromMinutes(1)));

// Back: the group's overrides, then all of them
await configurator.RemoveGroupAsync("user-data");
await configurator.ClearAsync();
```
<!-- endSnippet -->

The async operations return `ValueTask` and take cancellation last. These throw an `ArgumentException` before
anything is stored:

- a method id or expression that reaches no cached method, or a method that returns nothing (the message names the
  closest cached id), or a group no cached method is in (the message names the closest group);
- a tag template that doesn't fit a method the override reaches;
- an override that sets nothing, or sets `WithName`;
- an `ExpiresAt` that has already passed;
- a `RefreshAhead` or `Duration` that would leave a method refreshing its entries as soon as they are stored, such as
  a `RefreshAhead` of 20 minutes on a method whose `[Cache]` keeps entries for 10.

The group and all-methods versions check every method first, so a mistake for one method changes none of them.
- `UpsertGroupAsync` and `UpsertAllAsync` give each method an override of its own, replacing the one it had.
  `GetOverrides()` lists them per method, `RemoveAsync` removes one of them, and a method that joins the group later
  doesn't get it. Groups are case-sensitive, as everywhere.
- Tags from an override are added to the method's tags unless the override calls `ClearTags()`.
  `WithEnabled(false)` turns caching off for the method; `WithDistributed(false)` keeps it in memory only.
- An override that changes how the method caches drops the entries cached under the old settings at once; calls
  already running may still return their earlier result. An override that only changes its audit metadata keeps them.
- With a [distributed cache](distributed-cache.md) and its backplane (the default with `UseRedis`, `UseSqlServer` and
  `UsePostgreSql`), an override applies on every instance. It is stored in the distributed cache before `UpsertAsync`
  returns, the other instances apply it when the backplane tells them the method changed, and an instance that starts
  applies the active ones before it serves. So overrides outlive restarts and slot swaps, and `GetOverrides()` lists
  the same ones everywhere. Instances with the same override share its entries.
- An override that every instance applies can't set `CacheWhen`, `DurationFor`, `TagsFor` or `SlidingFor`: they are
  functions, which another instance can't run. `UpsertAsync` throws an `ArgumentException`; set them in `AddMethodCache`.
- When the distributed cache doesn't answer within `OperationTimeout`, `UpsertAsync`, `RemoveAsync` and `ClearAsync`
  throw an `InvalidOperationException` and the override stays as it was. An instance that can't read them when it starts logs a warning, starts
  without them and reads them again after `CircuitBreakerDuration`.
- Without a distributed cache, or with `EnableBackplane = false`, overrides live in this instance's memory: they are
  lost on restart and don't reach other instances, and they may set functions. While such an override changes how a
  method caches, that method's entries in the distributed cache are this instance's own; removing the override
  invalidates the method and shares entries again
  ([Distributed cache reference](../reference/distributed.md#policies-and-shared-entries)).
- `RuntimeOverrideMetadata` keeps owner, reason and ticket for the audit trail, and `ExpiresAt` removes the override
  when it passes.
- `MethodCache.AspNetCore` sets, lists and removes overrides over HTTP with a body written like a method's entry in
  `appsettings.json` ([Monitoring](monitoring.md#admin-endpoints)).
- A changed `appsettings.json` applies on this instance before the reload notification returns. Each instance reloads
  its own configuration; MethodCache doesn't distribute files.

## Seeing the effective policy

`ICacheAdministrator.GetMethods()` shows each method's effective options, where each setting comes from, and whether
its distributed cache entries are shared with other instances or its own because of a runtime override:

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
is left out. `ReplacedSources` lists a setting that two of the method's own sources set, with the lower ones the
higher one replaced: `Duration` with `[Attribute]` when startup code sets a `DurationFor` for a method whose `[Cache]`
sets a `Duration`. A `Defaults` or group setting the method replaces isn't listed, since that is what defaults are for.
Tags add up across layers, so `SettingSources["Tags"]` names the highest layer that adds tags. `Group` is the group
the method is in, or null. The admin endpoint `GET /methodcache/methods` shows the same, with the sources as strings
([Monitoring](monitoring.md)).

### What applies on this instance

`Options` holds what the method's sources set; a setting none of them sets is null there and missing from
`SettingSources`, and has the library's default. Next to it, each method says what that comes to on this instance,
with the defaults filled in:

<!-- snippet: configuration-what-applies -->
```cs
foreach (var method in diagnostics.GetMethods())
{
    // Defaults filled in: 5 minutes without a Duration, the stored copy's life, and why values stay local
    Console.WriteLine($"{method.MethodId}: in memory up to {method.Lifetime.Duration}, " +
        $"shared for {method.Lifetime.Distributed?.ToString() ?? "-"} ({method.Storage.MemoryOnlyReason ?? method.Storage.Store})");
    Console.WriteLine($"  key: {string.Join(", ", method.KeyParameters)}");
    foreach (var kept in method.NotShared)
        Console.WriteLine($"  {kept.Type} kept in memory {kept.Count} times since {kept.Since}: {kept.Reason}");
}
```
<!-- endSnippet -->

| Property | Shows |
|---|---|
| `Lifetime.Duration` | The longest an entry lives in memory: the method's `Duration`, from `Defaults` or its group, else five minutes; with only a sliding window, `MaxExpiration`. Null when `DurationFor` gives each result its own (`DurationFromResult`) |
| `Lifetime.SlidingExpiration` | The sliding window, or null without one or when `SlidingFor` gives each result its own (`SlidingFromResult`) |
| `Lifetime.MaxExpiration` | The longest any entry lives: a limit for the whole cache, not a setting of the method (7 days unless `mc.Memory` sets it) |
| `Lifetime.Distributed` | How long the copy in the distributed cache lives: the `Duration`, at most one sliding window and at most `TagRetention` less `ClockSkewTolerance`. Null when values aren't stored there or the result gives its own times |
| `Storage.Distributed` | Whether values are stored in the distributed cache as well as in memory |
| `Storage.Store` | The distributed cache, such as `Redis`, `SqlServer` or `PostgreSql` |
| `Storage.MemoryOnlyReason` | Why values stay in memory: no distributed cache, `Enabled` or `Distributed` false (and where), `StoreValuesByDefault = false`, or a synchronous method |
| `Storage.Shared` | Whether other instances read the values this one stores: false when values stay in memory, and while a runtime override that only this instance applies gives it a namespace of its own |
| `KeyParameters` | The parameters whose values make up the key, in order; a `CancellationToken` isn't part of it, and `Version` is |
| `NotShared` | Result types this instance kept in memory although the method shares its values, with the reason, how many and since when: a type the serializer can't write, or one that doesn't read back unchanged ([Serialization](distributed-cache.md#serialization)) |

`NotShared` is what this instance saw since it started; each instance keeps its own.

## Validation

| Setting | Rule | Checked |
|---------|------|---------|
| `Duration` | A positive time span, and with the built-in memory cache at most `MethodCacheMemoryOptions.MaxExpiration` (7 days by default) | `[Cache]`: at build time ([MC0004](../user-guide/DIAGNOSTICS.md#mc0004)). Code: `ArgumentOutOfRangeException`. JSON: at startup. Longer than `MaxExpiration`: at startup from `[Cache]`, code and JSON, an `ArgumentException` from a runtime override, and an `ArgumentOutOfRangeException` from a manual call |
| `SlidingExpiration`, `RefreshAhead`, `FailSafe` | A positive time span or `"off"`; in code, `TimeSpan.Zero` turns it off. A `SlidingExpiration` is at most `MaxExpiration`, like `Duration` | `[Cache]`: at build time ([MC0004](../user-guide/DIAGNOSTICS.md#mc0004)). Code: `ArgumentOutOfRangeException` for a negative value. JSON: at startup. A window longer than `MaxExpiration` fails where a longer `Duration` does |
| `RefreshAhead` | Shorter than `Duration` and `SlidingExpiration` | `[Cache]`: at build time ([MC0023](../user-guide/DIAGNOSTICS.md#mc0023)). Code: `InvalidOperationException` when the options are built. JSON: at startup. Runtime override: `ArgumentException`. When the two come from different layers, such as `RefreshAhead` in code and `Duration` in `Defaults`, the host logs a warning at startup and the cache refreshes halfway through the duration |
| `FailSafeTimeout`, `FailSafeRetryAfter` | A positive time span | `[Cache]`: at build time. Code (`WithFailSafeTimeout`, `WithFailSafeRetryAfter`): `ArgumentOutOfRangeException`. JSON: at startup |
| `Tags` | Not empty or whitespace; templates name parameters of the method | `[Cache]`: at build time. Code: `AddTags` throws an `ArgumentException` for an empty tag; templates at startup. JSON: at startup |
| `Version` | Zero or more | Code: `ArgumentOutOfRangeException`. JSON: at startup. On `[Cache]`, a negative explicit value is MC0021; omission inherits |

`CacheEntryOptions.Builder` applies to startup, configuration, runtime overrides and manual calls. Each setting has one
`With` method named like the attribute setting, such as `WithFailSafe`, `WithFailSafeTimeout` and
`WithFailSafeRetryAfter`. `CacheWhen<T>`, `DurationFor<T>`, `SlidingFor<T>` and `TagsFor<T>` are configured in code for one method; one for a type the
method can't return, such as `CacheWhen<string>` on a method that returns `int`, throws an `InvalidOperationException`
at startup (an `ArgumentException` from a runtime override) instead of silently caching nothing. Cache callbacks and
context predicates are not supported.

`[Cache]` takes named settings only. `Enabled` and `Distributed` inherit the configured policy when you leave them out,
and an explicit `false` wins over the lower layers. `FailSafeRetryAfter` sets how long a served fail-safe value is
reused before the method is tried again (30 seconds by default). `[assembly: CacheInterface(typeof(...), nameof(...))]`
takes the same named settings.

Overloaded generic methods have the number of type parameters in their method id: `Get(int)`, `Get``1(int)` and
`Get``2(int)` are three methods, in `appsettings.json` as well. Options and tags you pass in code are copied, so
changing the collection afterwards changes nothing.
