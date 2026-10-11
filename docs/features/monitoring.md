# Monitoring

MethodCache shows what it is doing in code, over HTTP, in health checks and through OpenTelemetry.

## In code

`ICacheAdministrator`, registered by `AddMethodCache()`, reads statistics, lists methods and entries, and invalidates:

<!-- snippet: monitoring-administrator -->
```cs
var stats = await admin.GetStatsAsync();
Console.WriteLine($"Hit ratio {stats.HitRatio:P0}, {stats.Entries} entries");

foreach (var method in admin.GetMethods())
{
    Console.WriteLine($"{method.MethodId}: {method.Duration}");
}

var entries = admin.ListEntries(tag: "user:42");
await admin.InvalidateTagsAsync(["user:42"]);
```
<!-- endSnippet -->

- `GetStatsAsync()`: hits, misses, hit ratio, evictions, entries and estimated memory.
- `GetMethods()`: every cached method with its method id and effective policy.
- `ListEntries(keyPrefix, tag, limit)` and `GetEntry(key)`: live entries, without their values.
- `InvalidateTagsAsync(tags)`, `RemoveEntryAsync(key)` and `ClearAsync()`.

It works with the default in-memory cache manager; a custom `ICacheManager` has no administrator.

## Admin endpoints

The `MethodCache.AspNetCore` package exposes the same operations over HTTP, behind an authorization policy you choose:

<!-- snippet: aspnetcore-setup -->
```cs
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddMethodCache();
builder.Services.AddHealthChecks().AddMethodCache();
builder.Services.AddAuthorization(o => o.AddPolicy("CacheAdmins", p => p.RequireRole("Admin")));

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapMethodCacheAdmin("CacheAdmins");
app.MapHealthChecks("/health");
```
<!-- endSnippet -->

`GET /methodcache/stats`, `/methods`, `/entries` and `/entry`, `POST /methodcache/invalidate` and `DELETE` for entries.
The [package README](../../src/MethodCache.AspNetCore/README.md) lists every endpoint. They don't change policies;
use [runtime overrides](configuration.md#runtime-overrides) for that.

## Health check

`AddHealthChecks().AddMethodCache()` reports Healthy when the cache answers, with `entries`, `hitRatio`, `evictions`
and `memoryUsageBytes` in the report data. With Redis or SQL Server it reports Degraded while that cache is
unreachable and MethodCache serves from memory only.

## OpenTelemetry

The `MethodCache.OpenTelemetry` package records metrics for every call (hits, misses, errors and load time per
method, and the size of the in-memory cache) and spans for misses and invalidations:

<!-- snippet: otel-setup -->
```cs
builder.Services.AddMethodCache();
builder.Services.AddMethodCacheOpenTelemetry(); // after AddMethodCache, which registers the cache it instruments

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddMethodCacheInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMethodCacheInstrumentation()
        .AddOtlpExporter());
```
<!-- endSnippet -->

Hits have no span, so they stay allocation-free, but they are counted. Cache keys are left off spans unless you turn
on `RecordCacheKeys`, because keys can contain argument values. See the
[package README](../../src/MethodCache.OpenTelemetry/README.md).
