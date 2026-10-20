# MethodCache.OpenTelemetry

Requires .NET 10 and the .NET 10 SDK; use the same package version as `MethodCache`.

Install this package and `MethodCache` at the exact same version. Integration packages use Core internals;
do not override their exact Core dependency. Package smoke tests promote NU1608 to a restore error.

Core produces standard .NET metrics and activities. This package provides just two SDK builder extensions,
`AddMethodCacheInstrumentation()` for `AddMeter(MethodCacheTelemetry.Name)` and `AddSource(MethodCacheTelemetry.Name)`.
The package depends on the OpenTelemetry API; your app supplies the SDK and exporters. It does not decorate the cache
manager or change its registration, lifetime, keyed services or disposal ownership.

## Setup

```bash
dotnet add package MethodCache.OpenTelemetry
dotnet add package OpenTelemetry.Extensions.Hosting
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol
```

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

SDK configuration and `AddMethodCache()` can appear in either order. Sampling, resources and export intervals are app
settings. Core uses the container's `IMeterFactory` and creates a container-owned activity source. The meter/source
name is `MethodCache`, available as `MethodCacheTelemetry.Name`; version comes from the assembly.

## Privacy options

Configure `MethodCacheTelemetryOptions` in Core:

<!-- snippet: otel-baggage -->
```cs
builder.Services.AddMethodCache(mc => mc.Telemetry(options =>
{
    options.RecordCacheKeys = true; // hashes by default; raw keys can contain secrets
    options.RecordCacheTags = false;
}));
```
<!-- endSnippet -->

| Option | Default | Meaning |
|---|---|---|
| `RecordCacheKeys` | `false` | Include keys on operation spans |
| `HashCacheKeys` | `true` | Record SHA-256 of lossless UTF-16 key bytes instead of raw keys when enabled |
| `RecordCacheTags` | `false` | Include application tags on operation spans |

Hashes are stable identifiers, not anonymization. Raw keys/tags can contain secrets. These options never add metric
dimensions. The library adds no user/tenant baggage, copies no request HTTP/URL attributes, and records no exception
messages or stack traces. Application-owned ambient .NET baggage/context still flows normally; the app controls its
propagation and export. See [Security](https://github.com/eoniclabs/MethodCache/blob/main/docs/security.md).

## Spans

| Span | Kind | Attributes |
|---|---|---|
| `MethodCache.load` | Internal | `methodcache.kind`, `methodcache.method` or `methodcache.http.client`, `methodcache.bulk`; `error.type` on failure |
| `MethodCache.distributed` | Internal | `methodcache.operation`; `error.type` on failure |
| `MethodCache.invalidate` | Internal | `methodcache.invalidation.type` |

Loads measure only actual origin work, including refreshes. Flight joiners and L2 hits create no origin span. Pure
hits create no spans. Failures set Error status without an exception description/event payload. Opt-in identities use
`methodcache.key`, `methodcache.keys` and `methodcache.tags`. `MethodCache.distributed` is an internal span, so the
client spans of the Redis and SqlClient instrumentations nest under it.

Every attribute is in the `methodcache.` namespace, apart from the standard `error.type`:

| Attribute | Values |
|---|---|
| `methodcache.kind` | `method` (a cached method), `manual` (an `ICacheManager` call by key), `http` (an `HttpClient` response) |
| `methodcache.method` | The method id, for `kind=method` |
| `methodcache.http.client` | The `HttpClient` name, for `kind=http` |
| `methodcache.bulk` | `true` on a manual `GetOrCreateManyAsync` batch |
| `methodcache.result` | `hit`, `miss`, `stale`, `revalidated`, `bypass` or `error` |
| `methodcache.invalidation.type` | `key`, `tag`, `method` or `clear` |
| `methodcache.invalidation.stage` | `local`, `distributed`, `queued`, `published` or `publication_queued`; on the queue gauge `storage` or `publication` |
| `methodcache.operation` | `get`, `set`, `remove` or `invalidate` |

A manual call has no attribute naming what was cached, so it adds no series per key.

## Metrics

| Instrument | Type / unit | Dimensions |
|---|---|---|
| `methodcache.requests` | Counter / `{request}` | `methodcache.kind`, `methodcache.method` or `methodcache.http.client`, `methodcache.bulk`, `methodcache.result` |
| `methodcache.load.duration` | Histogram / `s` | `methodcache.kind`, `methodcache.method` or `methodcache.http.client`, `methodcache.bulk`, `error.type` on failure |
| `methodcache.entries` | ObservableUpDownCounter / `{entry}` | `methodcache.kind=method/http`; actual locally resident entries |
| `methodcache.http.size` | ObservableUpDownCounter / `By` | Local HTTP storage cost; zero body storage in L2 mode |
| `methodcache.invalidations` | Counter / `{invalidation}` | `methodcache.invalidation.type`, `methodcache.invalidation.stage` |
| `methodcache.distributed.duration` | Histogram / `s` | `methodcache.operation`, `error.type` on failure |
| `methodcache.distributed.queue` | ObservableUpDownCounter / `{invalidation}` | `methodcache.invalidation.stage=storage/publication`; distinct pending targets |

The two histograms advise bucket boundaries in seconds, from 5 ms to 10 s (0.005, 0.01, 0.025, 0.05, 0.075, 0.1,
0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10), like ASP.NET Core's `http.server.request.duration`. A view in your SDK setup
can replace them.

Exactly one outcome is recorded per completed caller; a failed fast probe is not another miss. Background refresh has
its own load duration and no extra user request. Bulk calls count once and time the actual batch factory. A cached
`null` is a hit. `TryGetAsync` can read L2 and retains presence independently of the returned value.

`methodcache.method` values are method ids, including declared generic parameters; arguments and closed runtime types
do not create series. Invalidation stages count targets separately and must not be summed into an
operation count. Entry gauges exclude the bounded HTTP observation registry and are never global L2 estimates.
Method weight is exposed through administrator `TotalWeight` in configured units; no method-object byte estimate or
hit-ratio instrument is invented. Calculate ratios from counters in the dashboard.

## Troubleshooting

If no spans appear, subscribe on the tracer builder and check the SDK's parent sampling policy. If metrics are absent,
subscribe on the meter builder. Core works with ordinary `MeterListener`/`ActivityListener` without this package or an
SDK. Late subscribers see current local storage gauges; counters start when their listener attaches.

## License

MIT. See [LICENSE](https://github.com/eoniclabs/MethodCache/blob/main/LICENSE).
