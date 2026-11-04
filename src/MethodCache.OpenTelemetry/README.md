# MethodCache.OpenTelemetry

MethodCache produces standard .NET metrics and traces. This package adds `AddMethodCacheInstrumentation()` to the
OpenTelemetry tracer and meter builders, which subscribes to them (`AddMeter` and `AddSource` with
`MethodCacheTelemetry.Name`). Your app supplies the OpenTelemetry SDK and exporters.

Install it at the same version as `MethodCache`.

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

`AddOpenTelemetry()` and `AddMethodCache()` can come in either order. The meter and activity source are named
`MethodCache` (`MethodCacheTelemetry.Name`), so any `MeterListener` or `dotnet-counters monitor --counters MethodCache`
sees them without this package. For Prometheus, use OpenTelemetry's Prometheus exporter with
`AddMethodCacheInstrumentation()`.

## Privacy options

Configure `MethodCacheTelemetryOptions` in `AddMethodCache`:

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
| `HashCacheKeys` | `true` | Record a SHA-256 hash of each key instead of the key |
| `RecordCacheTags` | `false` | Include application tags on operation spans |

A hash is a stable identifier, not anonymization, and keys and tags can contain secrets. These options never add
metric dimensions. MethodCache adds no baggage, copies no HTTP or URL attributes, and records no exception messages or
stack traces; your application's own activity context and baggage flow as usual. See [Security](https://github.com/eoniclabs/MethodCache/blob/main/docs/security.md).

## Spans

| Span | Kind | Attributes |
|---|---|---|
| `MethodCache.load` | Internal | `methodcache.kind`, `methodcache.method` or `methodcache.http.client`, `methodcache.bulk`; `error.type` on failure |
| `MethodCache.distributed` | Internal | `methodcache.operation`; `error.type` on failure |
| `MethodCache.invalidate` | Internal | `methodcache.invalidation.type` |

`MethodCache.load` covers a run of your method, background refreshes included. Callers that join a running load,
values read from the distributed cache and memory hits create none. A failure sets the Error status without the
exception's message. With the privacy options above, keys and tags are recorded as `methodcache.key`,
`methodcache.keys` and `methodcache.tags`. `MethodCache.distributed` is an internal span, so the
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
| `methodcache.entries` | ObservableUpDownCounter / `{entry}` | `methodcache.kind=method/http`; entries in this instance's memory |
| `methodcache.http.size` | ObservableUpDownCounter / `By` | Bytes of HTTP responses in this instance's memory; 0 when they are stored in the distributed cache |
| `methodcache.invalidations` | Counter / `{invalidation}` | `methodcache.invalidation.type`, `methodcache.invalidation.stage` |
| `methodcache.distributed.duration` | Histogram / `s` | `methodcache.operation`, `error.type` on failure |
| `methodcache.distributed.queue` | ObservableUpDownCounter / `{invalidation}` | `methodcache.invalidation.stage=storage/publication`; distinct pending targets |

The two histograms advise bucket boundaries in seconds, from 5 ms to 10 s (0.005, 0.01, 0.025, 0.05, 0.075, 0.1,
0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10), like ASP.NET Core's `http.server.request.duration`. A view in your SDK setup
can replace them.

- Each call records one outcome. A background refresh records a load duration but no request. A bulk call counts once
  and times its one batch factory call. A cached `null` is a hit.
- `methodcache.method` is the method id (generic methods once, not per type argument); arguments never create series.
- Invalidation stages count the same targets at each stage; don't add them together.
- Entry gauges count this instance's memory only, not the distributed cache.
- There is no hit-ratio instrument: compute it from `methodcache.requests` in your dashboard. Memory weight is
  `TotalWeight` on `ICacheAdministrator`.

## Troubleshooting

If no spans appear, call `AddMethodCacheInstrumentation()` on the tracer builder and check the SDK's sampler. If no
metrics appear, call it on the meter builder. MethodCache works with any `MeterListener` or `ActivityListener` without
this package. Counters start when a listener subscribes; gauges show the current state at once.

## License

MIT. See [LICENSE](https://github.com/eoniclabs/MethodCache/blob/main/LICENSE).
