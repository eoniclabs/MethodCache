[← MethodCache documentation](../README.md)

# Monitoring

Core emits standard .NET metrics and activities. `AddMethodCache()` uses the container's `IMeterFactory`, registering
one when needed, and owns an `ActivitySource` with the same `MethodCacheTelemetry.Name` (`MethodCache`). Both use the
assembly version. Separate containers keep their observation callbacks separate.

## Which layers an instance uses

When the cache starts, MethodCache logs one `Information` line that names its layers: the memory cache with its
`MaxItems`, and the distributed cache with its store, `KeyPrefix` and backplane, or that there is none. Compare it
between instances and environments when they don't seem to share entries or invalidations:

```text
MethodCache caches in memory, up to 10000 entries per instance, and in the distributed cache Redis under the key
prefix 'methodcache:', with the Redis backplane telling other instances to drop their copy.
```

## Administration and statistics

`ICacheAdministrator`, registered for the built-in manager, exposes local statistics, methods and entries:

<!-- snippet: monitoring-administrator -->
```cs
var stats = await admin.GetStatsAsync();
var ratio = stats.Hits + stats.Misses == 0 ? 0 : (double)stats.Hits / (stats.Hits + stats.Misses);
Console.WriteLine($"Hit ratio {ratio:P0}, {stats.LocalMethodEntries} local method entries");
Console.WriteLine($"HTTP: {stats.LocalHttpEntries} local responses, {stats.LocalHttpBytes} stored bytes");
Console.WriteLine($"Method weight: {stats.TotalWeight} (configured weight units)");

foreach (var method in admin.GetMethods())
    Console.WriteLine($"{method.MethodId}: {method.Options.Duration}");

var entries = admin.ListEntries(tag: "user:42");
await admin.InvalidateTagsAsync(["user:42"]);
```
<!-- endSnippet -->

To read the statistics from outside, such as on a `/health` page, return them as JSON:

<!-- snippet: monitoring-stats-endpoint -->
```cs
// Hits, misses, evictions and entries of this instance, as JSON; protect it like any admin endpoint
app.MapGet("/health/cache", async (ICacheAdministrator admin, CancellationToken ct) =>
    Results.Ok(await admin.GetStatsAsync(ct)));
```
<!-- endSnippet -->

`Hits`, `Misses` and `Evictions` are local counters. Compute a hit ratio in the app or dashboard. Storage fields have
explicit scopes:

| Field | Meaning |
|---|---|
| `LocalMethodEntries` | Method results resident in this instance |
| `LocalHttpEntries` | HTTP representations resident in this instance |
| `LocalHttpBytes` | HTTP storage cost in bytes; separate from method weight |
| `ObservedDistributedEntries` | HTTP responses this instance recently saw in the distributed cache (at most 1024); not a count of what is stored there |
| `TotalWeight` | Resident method weight in the configured weigher's units; one per entry without a weigher |

Statistics never serialize results or inspect their properties to estimate bytes. HTTP storage/observation fields
remain zero until the separate HTTP store is registered.

`GetMethods()` returns each method's `MethodId`, its effective `Options`, the `SettingSources` each setting comes from
and `IsDistributedNamespaceShared`. `ListEntries(storedKeyPrefix, tag, limit, methodId)` and `GetEntry(storedKey)`
inspect live local entries without values; listing is a diagnostic scan, not a transaction. `CacheEntryInfo.MethodId`
names the method an entry belongs to (`null` for manual entries and HTTP responses), and `ListEntries(methodId: ...)`
lists exactly that method's entries. `IMethodCacheKeys.MethodId` returns the id of a call ([Cache keys](keys.md)).
`CacheEntryInfo.StoredKey` is the key as stored, which `GetEntry` and `RemoveEntryAsync` take. For a manual entry with a
`Version` it includes the version, so it is not the key you pass to `ICacheManager`; there, use the key and version you
cached it with. `InvalidateMethodAsync(methodId)` removes every entry of one method, on every instance; an id no
cached method has throws an `ArgumentException`. `RemoveEntryAsync`, `InvalidateTagsAsync`, `InvalidateMethodAsync`
and `ClearAsync` complete once a configured distributed cache has confirmed the change, and throw
`CacheInvalidationQueuedException` when it is applied in memory but still queued for the distributed cache. Its
`Result` holds the counts of the invalidation (`null` for `ClearAsync`). The cache retries on its own; don't repeat
the call. `GetInvalidationQueues()` reports `Queued` (waiting for the distributed cache) and `PublicationQueued`
(waiting for the backplane). A custom or NoOp manager has no built-in method administrator or fabricated storage
statistics; with `AddHttpCaching` there is one for HTTP responses only.

## Admin endpoints

The `MethodCache.AspNetCore` package exposes administration behind an explicit authorization policy:

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

The [package README](../../src/MethodCache.AspNetCore/README.md#endpoints) lists the endpoints. Policies are read-only;
use [runtime overrides](configuration.md#runtime-overrides) to change them.

Configure your application's authentication scheme as well as the authorization policy. The
[web demo](../../samples/MethodCache.WebDemo/) uses cookies and returns 401 for anonymous admin requests and
403 for signed-in callers without the `Admin` role outside Development. It has no sign-in page; its
Development environment allows anonymous admin requests so you can try the endpoints locally.
It also exposes Development-only `/demo/queries` and `/demo/reset` routes to count source reads and restore
the demonstration data; those routes are not part of the MethodCache admin package.
## Health check

`AddHealthChecks().AddMethodCache()` reports the selected `failureStatus` (default `Degraded`) when the distributed cache doesn't answer,
invalidations are waiting to be retried, or the backplane is connecting or disconnected. The distributed cache and the
backplane are reported separately. Reports include `managerCapability`,
`statisticsAvailable`, `distributedCacheAvailable` when applicable, `backplaneStatus`, `queued` and
`publicationQueued`, plus local storage statistics; the
[package README](../../src/MethodCache.AspNetCore/README.md#health-check) lists every key. Backend exception text is
omitted.

An incompatible distributed schema or retention contract reports `Unhealthy` with an actionable safe reason,
uses local memory only, and retains invalidations. A safe established retention differing from the configured
value reports `Degraded`. Admin mutations return 503 when distributed storage has not confirmed invalidation;
the cache continues owning retries.

An empty queue and a connected subscription do not acknowledge delivery to every node; backplane delivery remains
best effort.

## OpenTelemetry

The optional `MethodCache.OpenTelemetry` package provides `AddMethodCacheInstrumentation()` on meter and tracer
builders. These extensions only subscribe to Core's instruments; they do not wrap, replace or take ownership of your
manager. SDK setup may precede or follow `AddMethodCache()`, and repeated subscriptions do not multiply events.

<!-- snippet: otel-setup -->
```cs
builder.Services.AddMethodCache();

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

Each completed cache call has one `methodcache.requests` outcome, `methodcache.result`: `hit`, `miss`, `stale`,
`revalidated`, `bypass` or `error`. `methodcache.kind` is `method`, `manual` or `http`, and `methodcache.method` (a
method id) or `methodcache.http.client` (a client name) says which; manual calls by key carry neither. Every attribute
is in the `methodcache.` namespace, apart from `error.type`. `methodcache.load.duration` measures your method when it
actually runs, background refreshes included; callers that join a running load and values read from the distributed
cache record none. A bulk call has one outcome and measures its one batch factory call.

Histograms use seconds, with bucket boundaries from 5 ms to 10 s. Local entry/HTTP byte instruments are observable
up-down counters. Invalidation counters count
distinct targets at each stage, not operation totals; do not add stages together. See the
[full instrument table](../../src/MethodCache.OpenTelemetry/README.md#metrics).

Spans cover runs of your method, individual distributed operations and invalidation. Pure hits have no spans. Keys
and tags require explicit Core options; keys are hashed by default when enabled. Metrics never include keys, tags,
URLs, user ids or tenant ids. MethodCache adds no baggage and copies no HTTP or URL attributes; your application's
own activity context and baggage flow as usual.

## Standard .NET listeners

OpenTelemetry is optional. The meter and activity source are named `MethodCache` (`MethodCacheTelemetry.Name`), so
`dotnet-counters monitor --counters MethodCache` and any `MeterListener` see them. For Prometheus, use OpenTelemetry's
Prometheus exporter with `AddMethodCacheInstrumentation()`.

prometheus-net 8 exports them too, with no MethodCache setup: its meter adapter puts the meter name in front, as in
`methodcache_methodcache_requests{methodcache_result="hit"}`. It starts listening at the first scrape, exports counters
as gauges and uses its own histogram buckets instead of the ones above; OpenTelemetry's exporter keeps both.

With a `CollectorRegistry` of your own, start the meter adapter with the registry and a metric factory for it. With only
`Registry` set, no MethodCache series appear:

<!-- snippet: monitoring-prometheus-net-registry -->
```cs
// A registry of your own needs both settings: with only Registry, no MethodCache series appear
var adapter = MeterAdapter.StartListening(new MeterAdapterOptions
{
    Registry = registry,
    MetricFactory = Metrics.WithCustomRegistry(registry),
});
```
<!-- endSnippet -->

A `MeterListener` subscribes like this; keep it alive for as long as you want measurements:

<!-- snippet: monitoring-console-metrics -->
```cs
builder.Services.AddMethodCache();
using var listener = new MeterListener();
listener.InstrumentPublished = (instrument, active) =>
{
    if (instrument.Meter.Name == MethodCacheTelemetry.Name)
        active.EnableMeasurementEvents(instrument);
};
listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
    Console.WriteLine($"{instrument.Name}: {value}"));
listener.Start(); // keep the listener alive for as long as measurements are needed
```
<!-- endSnippet -->

The privacy options are set in `AddMethodCache` and apply whatever listens:

<!-- snippet: monitoring-metrics-provider-registration -->
```cs
// Core emits standard Meter and ActivitySource instruments
builder.Services.AddMethodCache(mc => mc.Telemetry(options =>
{
    options.RecordCacheKeys = false;
    options.RecordCacheTags = false;
}));
```
<!-- endSnippet -->

With your own `ICacheManager`, MethodCache still measures whether a cached method's factory ran; anything else your
manager does is its own to instrument.
