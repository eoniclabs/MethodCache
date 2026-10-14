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
- `GetMethods()`: every cached method with immutable effective options, setting sources and L2 namespace kind.
- `GetInvalidationQueues()`: pending distributed targets and pending backplane publications.
- `ListEntries(keyPrefix, tag, limit)` and `GetEntry(key)`: live entries, without their values.
- `InvalidateTagsAsync(tags)`, `RemoveEntryAsync(key)` and `ClearAsync()`.

`ListEntries` returns entries in key order. It is a diagnostic scan, not a transaction: entries can change while it
runs.

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

The [package README](../../src/MethodCache.AspNetCore/README.md#endpoints) lists every endpoint. They don't change
policies; use [runtime overrides](configuration.md#runtime-overrides) for that.

## Health check

`AddHealthChecks().AddMethodCache()`, from the same package, reports Degraded while Redis or SQL Server is unreachable
and MethodCache serves from memory only. The [package README](../../src/MethodCache.AspNetCore/README.md#health-check)
lists the data it reports.

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

Hits have no span and add no allocations. Cache keys and tags are left off spans unless you turn on `RecordCacheKeys`
or `RecordCacheTags`, because they can contain argument values. The
[package README](../../src/MethodCache.OpenTelemetry/README.md) lists the options, span attributes and metrics; see
also [Security](../security.md#methodcacheopentelemetry).

## Your own metrics

Without OpenTelemetry, cache events go to an `ICacheMetricsProvider` (in `MethodCache.Extensibility`). The default
one discards them. To see them on the console while you develop:

<!-- snippet: monitoring-console-metrics -->
```cs
builder.Services.AddMethodCache();
builder.Services.AddConsoleCacheMetrics();   // "Cache Hit: ...", "Cache Miss: ..." on standard output
```
<!-- endSnippet -->

Or implement the interface. Each cached call is reported once, as a hit or a miss; errors and load times are reported
when your method runs. `methodName` is the method id, such as `MyApp.IUserService.GetUserAsync`, the same id the admin
endpoints list (a generic interface reports its closed type, such as `IRepository<User>.Get`):

<!-- snippet: monitoring-metrics-provider -->
```cs
public sealed class CountingMetrics : ICacheMetricsProvider
{
    private long _hits, _misses, _errors;

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public long Errors => Interlocked.Read(ref _errors);

    public void CacheHit(string methodName) => Interlocked.Increment(ref _hits);
    public void CacheMiss(string methodName) => Interlocked.Increment(ref _misses);
    public void CacheError(string methodName, string errorMessage) => Interlocked.Increment(ref _errors);
    public void CacheLatency(string methodName, TimeSpan elapsed) { }   // load time after a miss
}
```
<!-- endSnippet -->

<!-- snippet: monitoring-metrics-provider-registration -->
```cs
builder.Services.AddMethodCache();
builder.Services.AddSingleton<ICacheMetricsProvider, CountingMetrics>();
```
<!-- endSnippet -->

One provider receives the events: the last one registered. `AddMethodCacheOpenTelemetry()` registers its own, so use
it or a provider of your own, not both; with `EnableMetrics = false` it leaves yours in place and adds only spans. A custom `ICacheManager` reports its own hits and misses. If you wrap
`ICacheManager` in a decorator of your own, implement `ICacheManagerDecorator` so `GetOrCreateManyAsync` still reads
the distributed cache through it.

OpenTelemetry instruments the unkeyed `ICacheManager` registration and leaves unrelated keyed managers untouched. A keyed-only registration does not satisfy its requirement for a default manager.

OpenTelemetry preserves the original cache manager registration lifetime and DI disposal ownership. Type/factory-created managers remain container-owned; supplied instances remain caller-owned, including asynchronous disposal.

An instrumented cache lookup generates the application cache key once and reuses it for tracing and the presence-aware inner lookup, preserving the original method and arguments without a second serialization/hash pass.

The health report includes `invalidationQueued` and `publicationQueued`, and reports Degraded while either recovery
queue has work. Backplane delivery remains best effort; empty queues do not acknowledge receipt by all nodes.
