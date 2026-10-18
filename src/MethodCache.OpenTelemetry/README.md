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
builder.Services.Configure<MethodCacheTelemetryOptions>(options =>
{
    options.RecordCacheKeys = true; // hashes by default; raw keys can contain secrets
    options.RecordCacheTags = false;
});
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
| `MethodCache.load` | Internal | `kind`, `name`; `error.type` on failure |
| `MethodCache.distributed` | Client | `operation`; `error.type` on failure |
| `MethodCache.invalidate` | Internal | `type` |

Loads measure only actual origin work, including refreshes. Flight joiners and L2 hits create no origin span. Pure
hits create no spans. Failures set Error status without an exception description/event payload. Opt-in identities use
`methodcache.key`, `methodcache.keys` and `methodcache.tags`.

## Metrics

| Instrument | Type / unit | Dimensions |
|---|---|---|
| `methodcache.requests` | Counter / `{request}` | `kind=method/http`, `name=method-id/client`, `result=hit/miss/stale/revalidated/bypass/error` |
| `methodcache.load.duration` | Histogram / `s` | `kind`, `name`, `error.type` on failure |
| `methodcache.entries` | ObservableUpDownCounter / `{entry}` | `kind`; actual locally resident entries |
| `methodcache.http.size` | ObservableUpDownCounter / `By` | Local HTTP storage cost; zero body storage in L2 mode |
| `methodcache.invalidations` | Counter / `{invalidation}` | `type=key/tag/method`, `stage=local/distributed/queued/published/publication_queued` |
| `methodcache.distributed.duration` | Histogram / `s` | `operation=get/set/remove/invalidate`, `error.type` on failure |
| `methodcache.distributed.queue` | ObservableUpDownCounter / `{invalidation}` | `stage=storage/publication`; distinct pending targets |

Exactly one outcome is recorded per completed caller; a failed fast probe is not another miss. Background refresh has
its own load duration and no extra user request. Bulk calls count once and time the actual batch factory. A cached
`null` is a hit. `TryGetAsync` can read L2 and retains presence independently of the returned value.

Generated `name` values use canonical declared method ids, including declared generic parameters; arguments and closed
runtime types do not create series. Invalidation stages count targets separately and must not be summed into an
operation count. Entry gauges exclude the bounded HTTP observation registry and are never global L2 estimates.
Method weight is exposed through administrator `TotalWeight` in configured units; no method-object byte estimate or
hit-ratio instrument is invented. Calculate ratios from counters in the dashboard.

## Troubleshooting

If no spans appear, subscribe on the tracer builder and check the SDK's parent sampling policy. If metrics are absent,
subscribe on the meter builder. Core works with ordinary `MeterListener`/`ActivityListener` without this package or an
SDK. Late subscribers see current local storage gauges; counters start when their listener attaches.

## License

MIT. See [LICENSE](https://github.com/eoniclabs/MethodCache/blob/main/LICENSE).
