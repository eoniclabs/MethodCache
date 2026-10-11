# MethodCache Configuration Guide

This guide explains how to configure MethodCache: attributes, startup code, `appsettings.json` and runtime overrides, and how they combine.

## Table of Contents

1. [Configuration Overview](#configuration-overview)
2. [Attribute-Based Configuration](#attribute-based-configuration)
3. [Manual Caching](#manual-caching)
4. [Programmatic Configuration](#programmatic-configuration)
5. [JSON Configuration](#json-configuration)
6. [Runtime Overrides](#runtime-overrides)
7. [Inspecting Policies and the Cache](#inspecting-policies-and-the-cache)
8. [Configuration Priorities](#configuration-priorities)
9. [Configuration Validation](#configuration-validation)

## Configuration Overview

Every cached method gets its policy (duration, sliding expiration, refresh-ahead, tags, version) from up to four sources:

| Source | How you use it | Priority | Changes while running |
|--------|----------------|----------|-----------------------|
| Attributes | `[Cache(...)]` on interface methods | 10 (lowest) | No |
| Startup code | `AddMethodCache(config => ...)` | 40 | No |
| Configuration | `AddMethodCacheFromConfiguration(configuration)` | 50 | Yes, on configuration reload |
| Runtime overrides | `IRuntimeCacheConfigurator` | 100 (highest) | Yes |

When several sources configure the same method, they are merged field by field and the higher priority wins for each field. Generated decorators read the merged policy on every call, so changes from configuration reloads and runtime overrides reach services that already exist, including singletons.

## Attribute-Based Configuration

The simplest way to configure caching is with attributes on interface methods:

```csharp
public interface IUserService
{
    [Cache("user-profile", Duration = "01:00:00", Tags = new[] { "user", "profile" })]
    Task<UserProfile> GetUserProfileAsync(int userId);

    [Cache("user-settings", Duration = "00:30:00", SlidingExpiration = "00:05:00", Tags = new[] { "user:{userId}" })]
    Task<UserSettings> GetUserSettingsAsync(int userId);

    [CacheInvalidate(Tags = new[] { "user", "user:{userId}" })]
    Task UpdateUserAsync(int userId, UserUpdateModel update);
}

// Register the service, then wrap it in its generated cache decorator
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddMethodCache();
```

`[Cache]` takes an optional group name and the properties `Duration`, `SlidingExpiration`, `RefreshAhead` (strings in `TimeSpan` format), `Tags` and `Version`. Tags can contain templates such as `"user:{userId}"`, which are filled in from the method's arguments.

## Manual Caching

Use `ICacheManager.GetOrCreateAsync` with an explicit key when attributes are not practical, such as third-party integrations or legacy code paths.

```csharp
public class UserService
{
    private readonly ICacheManager _cache;
    private readonly IUserRepository _repository;

    public ValueTask<User> GetUserAsync(int userId) =>
        _cache.GetOrCreateAsync(
            $"user:{userId}",
            async (ctx, ct) => await _repository.GetUserAsync(userId),
            o => o.WithDuration(TimeSpan.FromHours(1))
                  .WithTags("user", $"user:{userId}"));
}
```

`GetOrCreateAsync` accepts every option, including callbacks and a predicate that only make sense per call:

```csharp
var orders = await _cache.GetOrCreateAsync(
    $"orders:{customerId}:{status}",
    async (ctx, ct) => await _orderService.GetOrdersAsync(customerId, status),
    o => o.WithDuration(TimeSpan.FromMinutes(30))
          .WithSlidingExpiration(TimeSpan.FromMinutes(10))
          .RefreshAhead(TimeSpan.FromMinutes(5))
          .WithTags("orders", $"customer:{customerId}")
          .WithVersion(2)
          .OnHit(ctx => _logger.LogInformation("Cache hit: {Key}", ctx.Key))
          .OnMiss(ctx => _logger.LogInformation("Cache miss: {Key}", ctx.Key))
          .When(ctx => customerId > 0)); // skip the cache when the predicate is false
```

`WithVersion` changes the stored key, so bumping it invalidates older entries. See the [Fluent API Guide](FLUENT_API.md) for bulk and streaming helpers.

## Programmatic Configuration

Configure `[Cache]` methods in code:

```csharp
builder.Services.AddMethodCache(config =>
{
    // Base settings for every method listed below
    config.DefaultPolicy(options => options.WithDuration(TimeSpan.FromMinutes(15)));

    // Settings for the listed methods assigned to this group with WithGroup("user-data")
    config.ForGroup("user-data")
          .Configure(options => options
              .WithSlidingExpiration(TimeSpan.FromMinutes(10))
              .WithTags("user"));

    // Method-specific configuration
    config.ForService<IUserService>()
          .Method(x => x.GetUserProfileAsync(default))
          .WithGroup("user-data")
          .WithVersion(5)
          .Configure(options => options
              .WithDuration(TimeSpan.FromHours(1))
              .RefreshAhead(TimeSpan.FromMinutes(5))
              .WithTags("user", "profile"));
});
```

Each setting comes from the most specific place that sets it: the method itself (its `[Cache]` attribute, `ForService<T>().Method(...)` or JSON), then its group, then the defaults. `DefaultPolicy` and the JSON `Defaults` apply to every cached method, also ones that only have `[Cache]`, so `[Cache(Duration = "00:10:00")]` keeps its ten minutes while the defaults fill in what it leaves out. A method joins a group with `[Cache("name")]`, `WithGroup("name")` or `Group` in JSON, and gets that group's `ForGroup("name")` settings.

Generated decorators build readable keys themselves when every argument is a simple value (string, number, `bool`, `Guid`, date, enum), an `ICacheKeyProvider`, or a record, class or struct whose public properties and fields are simple values, for example `MyApp.IUserService.Search:(alice:2)`. Other arguments, such as collections, go through the key generator. `FastHashKeyGenerator` is the default; replace it through dependency injection:

```csharp
builder.Services.AddMethodCache();
builder.Services.Replace(ServiceDescriptor.Singleton<ICacheKeyGenerator, JsonKeyGenerator>());
```

## JSON Configuration

Configure cached methods from `appsettings.json` or any other `IConfiguration` source:

### appsettings.json
```json
{
  "MethodCache": {
    "Defaults": {
      "Duration": "00:15:00",
      "Tags": ["default"]
    },
    "Services": {
      "MyApp.Services.IUserService.GetUserProfileAsync": {
        "Duration": "01:00:00",
        "SlidingExpiration": "00:10:00",
        "RefreshAhead": "00:05:00",
        "Tags": ["user", "profile"],
        "Version": 1,
        "Notes": "Profiles change rarely"
      },
      "MyApp.Services.IProductService.GetProductAsync": {
        "Duration": "00:30:00",
        "Tags": ["product", "catalog"]
      }
    }
  }
}
```

Each key under `Services` is the method id: the interface's full name, a dot, and the method name. Supported settings are `Duration`, `SlidingExpiration`, `RefreshAhead`, `Tags`, `Version`, `Group`, `Metadata` and `Notes`. `Defaults` applies to every cached method, also ones not listed under `Services`, and a method's own settings, including those in its `[Cache]` attribute, win. `Group` puts the method in a `ForGroup(...)` group; `Metadata` and `Notes` are recorded with the policy for diagnostics and do not change caching.

Only methods with a `[Cache]` attribute have a generated decorator, so the file can change their policy but cannot make other methods cached.

### Usage
```csharp
builder.Services.AddMethodCache();
builder.Services.AddMethodCacheFromConfiguration(builder.Configuration); // reads the "MethodCache" section
```

Pass a section name as the second argument to read a different section. Environment-specific files such as `appsettings.Production.json` work as usual, because they are part of `IConfiguration`.

Changes to the section take effect while the application runs, as soon as the configuration reloads (appsettings.json reloads on save by default). Methods added, changed or removed in the file are picked up without a restart.

Invalid values, such as a `Duration` that is not a positive time span or a negative `Version`, make `AddMethodCacheFromConfiguration` throw at startup. If a reloaded file has an invalid value, the reload is logged as an error and the previous policies stay in effect.

## Runtime Overrides

Runtime overrides have the highest priority. Use them to change a method's policy without a deploy, for example to shorten a duration during an incident. Resolve `IRuntimeCacheConfigurator`, which `AddMethodCache()` registers:

```csharp
var configurator = app.Services.GetRequiredService<IRuntimeCacheConfigurator>();

// Strongly typed
await configurator.UpsertAsync<IOrdersService, Order>(
    s => s.GetAsync(default),
    o => o.WithDuration(TimeSpan.FromMinutes(2)),
    new RuntimeOverrideMetadata(Owner: "ops", Reason: "Incident 4211"));

// Several methods at once, with the fluent configuration API
await configurator.ApplyAsync(fluent =>
{
    fluent.ForService<IUserService>()
          .Method(s => s.GetUserProfileAsync(default))
          .Configure(o => o.WithDuration(TimeSpan.FromMinutes(5)));
});

// By method id, for example from an admin form
await configurator.UpsertAsync("MyApp.Services.IUserService.GetUserProfileAsync",
    (CacheEntryOptions.Builder o) => o.WithDuration(TimeSpan.FromMinutes(10)));

// Remove one override, or all of them, to fall back to the lower-priority sources
await configurator.RemoveAsync<IOrdersService, Order>(s => s.GetAsync(default));
await configurator.ClearAsync();
```

| Method | What it does |
|--------|--------------|
| `UpsertAsync(methodId, Action<CacheEntryOptions.Builder>, metadata?)` | Applies or replaces the override for one method. Overloads take a method expression, a `CachePolicyBuilder` or a `CachePolicy`. |
| `UpsertBatchAsync(IEnumerable<RuntimeOverrideEntry>)` | Applies several overrides in one call. |
| `ApplyAsync(Action<IFluentMethodCacheConfiguration>)` | Applies overrides for the methods listed with `ForService<T>().Method(...)`, using the same rules as startup code. |
| `RemoveAsync(methodId)` / `ClearAsync()` | Removes one override, or all of them. |
| `GetOverridesAsync()` / `GetOverrideAsync(methodId)` | Returns the active overrides. |
| `WatchAsync(cancellationToken)` | Streams override changes. |

`RuntimeOverrideMetadata` (owner, reason, ticket, expiry) is stored with the override for auditing. An override whose `ExpiresAt` has passed is removed the next time the runtime configurator is used. There is no timer, so until then the expired override stays in effect.

Runtime overrides are held in memory. They are lost on restart and are not shared between instances, so persist and replay them yourself if you need that. Overrides cannot disable caching for a method: `WithDuration` requires a positive duration. To get rid of stale data, invalidate it instead (see below).

## Inspecting Policies and the Cache

`PolicyDiagnosticsService` shows the effective policy of each method and which sources contributed to it:

```csharp
var diagnostics = app.Services.GetRequiredService<PolicyDiagnosticsService>();

foreach (var report in diagnostics.GetAllPolicies())
{
    var sources = string.Join(", ", report.Contributions.Select(c => c.SourceId).Distinct());
    Console.WriteLine($"{report.MethodId}: Duration={report.Policy.Duration}, Sources={sources}");
}
```

`GetPolicy(methodId)` returns one method's report, and `FindBySource(sourceId)` returns the methods a source configured. Source ids are `attributes`, `startup-fluent`, `configuration-files` and `runtime-overrides`.

To inspect and empty the cache itself, use `ICacheAdministrator` (stats, cached methods, entries, invalidation by tag). The `MethodCache.AspNetCore` package exposes the same operations over HTTP:

```csharp
builder.Services.AddHealthChecks().AddMethodCache();

app.MapMethodCacheAdmin().RequireAuthorization("CacheAdmins"); // GET /methodcache/stats, /methods, /entries, ...
```

See the [MethodCache.AspNetCore README](../../MethodCache.AspNetCore/README.md) for the endpoints. They do not change policies; use `IRuntimeCacheConfigurator` for that.

## Configuration Priorities

The priorities are defined in `PolicySourcePriority`:

| Priority | Source | Overrides |
|----------|--------|-----------|
| 100 | Runtime overrides (`RuntimeOverrides`) | Everything below |
| 50 | Configuration such as appsettings.json (`ConfigurationFiles`) | Startup code and attributes |
| 40 | Startup code, `AddMethodCache(config => ...)` (`StartupFluent`) | Attributes |
| 10 | `[Cache]` attributes (`Attributes`) | Nothing |

Example for one method:

```csharp
// Attribute: 1 hour
[Cache(Duration = "01:00:00", Tags = new[] { "users" })]
Task<UserProfile> GetUserProfileAsync(int userId);

// Startup code: 2 hours
config.ForService<IUserService>()
      .Method(x => x.GetUserProfileAsync(default))
      .Configure(o => o.WithDuration(TimeSpan.FromHours(2)));

// appsettings.json: 30 minutes
// "MyApp.Services.IUserService.GetUserProfileAsync": { "Duration": "00:30:00" }

// Runtime override: 5 minutes
await configurator.UpsertAsync<IUserService, UserProfile>(
    s => s.GetUserProfileAsync(default),
    o => o.WithDuration(TimeSpan.FromMinutes(5)));

// Result: Duration = 5 minutes (runtime override), Tags = ["users"] (only the attribute set them)
```

## Configuration Validation

| Setting | Rule | Where it is checked |
|---------|------|---------------------|
| `Duration`, `SlidingExpiration`, `RefreshAhead` | Positive time span | `[Cache]`: compile time (error MC0004). Builders: `ArgumentOutOfRangeException`. JSON: startup |
| `Version` | Zero or more | Builders: `ArgumentOutOfRangeException`. JSON: startup |

```csharp
// Throws ArgumentOutOfRangeException
config.ForService<IUserService>()
    .Method(x => x.GetUserProfileAsync(default))
    .Configure(options => options.WithDuration(TimeSpan.Zero));
```

### Settings That Only Apply to GetOrCreateAsync

`OnHit`, `OnMiss`, `WithMetrics` and `When` run around a single `GetOrCreateAsync` call. Cached `[Cache]` methods cannot use them, so configuring them through `DefaultPolicy`, `ForGroup` or `ForService(...).Method(...)` throws `InvalidOperationException` at startup rather than being silently ignored.
