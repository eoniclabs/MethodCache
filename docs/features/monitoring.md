[← MethodCache documentation](../README.md)

# Monitoring

Core emits standard .NET metrics and activities. `AddMethodCache()` uses the container's `IMeterFactory`, registering
one when needed, and owns an `ActivitySource` with the same `MethodCacheTelemetry.Name` (`MethodCache`). Both use the
assembly version. Separate containers keep their observation callbacks separate.

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

`Hits`, `Misses` and `Evictions` are local counters. Compute a hit ratio in the app or dashboard. Storage fields have
explicit scopes:

| Field | Meaning |
|---|---|
| `LocalMethodEntries` | Method results resident in this instance |
| `LocalHttpEntries` | HTTP representations resident in this instance |
| `LocalHttpBytes` | HTTP storage cost in bytes; separate from method weight |
| `ObservedDistributedEntries` | Bounded HTTP observations, never a global L2 count |
| `TotalWeight` | Resident method weight in the configured weigher's units; one per entry without a weigher |

Statistics never serialize results or inspect their properties to estimate bytes. HTTP storage/observation fields
remain zero until the separate HTTP store is registered.

`GetMethods()` returns immutable effective options, sources and L2 namespace kind. `ListEntries(keyPrefix, tag, limit)`
and `GetEntry(key)` inspect live local entries without values; listing is a diagnostic scan, not a transaction.
`GetInvalidationQueues()` distinguishes storage recovery from pending backplane publication. A custom or NoOp manager
has no built-in method administrator or fabricated storage statistics.

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
The sample price endpoint rejects missing, null and negative prices before writes or invalidation; rejected requests
leave both stored products and the cache unchanged.

## Health check

`AddHealthChecks().AddMethodCache()` reports the selected `failureStatus` (default `Degraded`) when L2 is unavailable,
either recovery queue has work, or a configured backplane is unknown, connecting or disconnected. A successful L2
probe does not prove that invalidation subscription is established. Reports include `managerCapability`,
`statisticsAvailable`, `distributedCacheAvailable` when applicable, `backplaneStatus`, `invalidationQueued` and
`publicationQueued`, plus local storage statistics for the built-in manager. Backend exception text is omitted.

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

Each completed cache call has one `methodcache.requests` outcome: `hit`, `miss`, `stale`, `revalidated`, `bypass` or
`error`, with `kind=method/http` and a static method id/client name. A failed fast probe is not a separate miss.
Refreshes record their actual origin duration without another user request. Flight joiners and L2 hits do not record
origin loads. A bulk call has one outcome and measures its actual batch factory, rather than the per-key publication
of the already produced values.

Histograms use seconds. Local entry/HTTP byte instruments are observable up-down counters. Invalidation counters count
distinct targets at each stage, not operation totals; do not add stages together. See the
[full instrument table](../../src/MethodCache.OpenTelemetry/README.md#metrics).

Spans cover actual origin loads, individual distributed operations and invalidation. Pure hits have no spans. Keys
and tags require explicit Core options; keys are hashed by default when enabled. Metrics never include keys, tags,
URLs, user ids or tenant ids. The library adds no baggage and copies no request HTTP/URL attributes. Normal ambient
.NET activity context and application-owned baggage continue to flow; configure their export in the app.

## Standard .NET listeners

OpenTelemetry is optional. A `MeterListener` can subscribe directly; retain it for the measurement lifetime:

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

Identity options belong to Core and do not depend on an exporter:

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

For generated calls through a custom manager, Core can observe whether its supplied factory ran. Standalone calls to
an application-owned manager must be instrumented by that implementation; its storage capabilities are not assumed.
