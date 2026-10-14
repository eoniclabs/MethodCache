# MethodCache.OpenTelemetry

OpenTelemetry metrics and traces for MethodCache.

The package depends only on the OpenTelemetry API, as instrumentation libraries should, so it works the same in
console apps, workers and ASP.NET Core. Your app brings the SDK and exporters. For request spans, add
`OpenTelemetry.Instrumentation.AspNetCore` to your app as usual.

## What you get

- **Metrics for every call**:
  - hits, misses and errors per method;
  - how long the cached method took on a miss;
  - entries, memory and hit ratio of the in-memory cache.
- **Spans for misses and invalidations**:
  - Each span has the method, hit or miss, TTL, group and version. Keys and tags only when you turn them on, since they contain argument values.
  - Spans are children of the current request span and copy its `http.*` and `url.*` attributes.
- **Hits stay allocation-free**: generated decorators answer hits from the cache's fast path, which has no span. Hits still count in `methodcache.hits_total`.

A cached `null` counts as a hit. On `TryGetAsync` spans, `cache.hit` means found in memory: `TryGetAsync` never reads
the distributed cache.

## Installation

```bash
dotnet add package MethodCache.OpenTelemetry
dotnet add package OpenTelemetry.Extensions.Hosting   # the SDK and AddOpenTelemetry(), if your app doesn't have it yet
```

## Setup

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

`AddMethodCacheOpenTelemetry()` throws `InvalidOperationException` when it is called before `AddMethodCache()`.
Sampling, export intervals and the service name are set on the OpenTelemetry SDK as for any other library.

## Options

Pass options to the registration, for example `AddMethodCacheOpenTelemetry(o => o.RecordCacheKeys = true)`.

| Option | Default | What it does |
|--------|---------|--------------|
| `EnableTracing` | `true` | Creates spans |
| `EnableMetrics` | `true` | Records metrics |
| `RecordCacheKeys` | `false` | Puts the cache key on spans. Keys can contain argument values |
| `HashCacheKeys` | `true` | With `RecordCacheKeys`, records a SHA-256 hash of the key instead of the key |
| `RecordCacheTags` | `false` | Puts the cache tags and invalidation patterns on spans. Tags such as `user:{userId}` contain argument values |
| `ExportSensitiveData` | `false` | Puts the user and tenant ids from `IBaggagePropagator` into span baggage |
| `EnableHttpCorrelation` | `true` | Copies the request's `http.*` and `url.*` attributes onto cache spans |
| `EnableBaggagePropagation` | `true` | Carries `cache.*` baggage between spans |

## Baggage

`IBaggagePropagator` carries a correlation id, an invalidation id, a user id and a tenant id along the async flow,
and puts them into the baggage of cache spans, which travels to downstream services. The user and tenant ids
identify people, so they are left out unless `ExportSensitiveData` is on. A value set in a child task stays in that
task.

<!-- snippet: otel-baggage -->
```cs
_baggagePropagator.SetCacheCorrelationId(Guid.NewGuid().ToString());
_baggagePropagator.SetCacheTenantId(tenantId); // exported only with ExportSensitiveData = true
```
<!-- endSnippet -->

## Span attributes

Spans come from the `MethodCache` activity source. Lookups and misses are named `MethodCache.cache.get`, invalidations
`MethodCache.cache.delete`.

| Attribute | Description | Example |
|-----------|-------------|---------|
| `cache.operation` | `cache.get` or `cache.delete` | `cache.get` |
| `cache.method` | Method id, or the invalidation call | `MyApp.IUserService.GetUserAsync` |
| `cache.hit` | Whether the value came from the cache | `true` |
| `cache.key` | The key, with `RecordCacheKeys` and `HashCacheKeys = false` | `MyApp.IUserService.GetUserAsync:42` |
| `cache.key_hash` | Base64 SHA-256 of the key, with `RecordCacheKeys` | `3q2+7w...=` |
| `cache.ttl_seconds` | Time-to-live | `600` |
| `cache.tags` | Cache tags, with `RecordCacheTags` | `user,profile` |
| `cache.pattern` | Invalidation pattern, with `RecordCacheTags` | `user:*` |
| `cache.group` | Group | `users` |
| `cache.version` | Version | `2` |
| `cache.keys_count` | Number of keys invalidated | `3` |
| `cache.error` | Set when the operation failed | `true` |
| `cache.error_type` | Exception type name | `TimeoutException` |

A failed operation also sets the span status to Error and adds an `exception` event with `exception.type`,
`exception.message` and `exception.stacktrace`.

## Metrics

Metric dimensions are kept to values with few variants (`method`, `group`, `version`, `error.type`), so a tag such
as `user:42` never becomes a time series of its own. `method` is the method id that `GET /methodcache/methods` and
`appsettings.json` use, such as `MyApp.IUserService.GetUserAsync`, so `IUserService.GetAsync` and
`IOrderService.GetAsync` are two series; a generic interface reports its closed type, such as `IRepository<User>.Get`.

| Metric | Type | Description | Dimensions |
|--------|------|-------------|------------|
| `methodcache.hits_total` | Counter | Cache hits, including fast-path hits | method |
| `methodcache.misses_total` | Counter | Cache misses | method |
| `methodcache.errors_total` | Counter | Errors: `error.type` is `load_failed` when the cached method throws, otherwise the exception type name | method, error.type |
| `methodcache.load_duration` | Histogram (ms) | Time the cached method took on a miss or refresh | method |
| `methodcache.operation_duration` | Histogram (ms) | Duration of instrumented operations (misses, invalidations) | method, group, version |
| `methodcache.key_generation_duration` | Histogram (ms) | Key generation on instrumented operations | method |
| `methodcache.entries_count` | Gauge | Entries in the in-memory cache | |
| `methodcache.memory_bytes` | Gauge | Estimated memory of the in-memory cache | |
| `methodcache.hit_ratio` | Gauge | Hits / (hits + misses) | |

## Troubleshooting

**No spans.** Check that the SDK listens to the source with `.AddMethodCacheInstrumentation()` on the tracer
provider. `TracingConstants.ActivitySource.HasListeners()` is true once it does.

**No HTTP attributes on cache spans.** Cache spans copy them from the request span, so enable ASP.NET Core
instrumentation: `.WithTracing(t => t.AddAspNetCoreInstrumentation().AddMethodCacheInstrumentation())`.

## License

MIT. See [LICENSE](https://github.com/eoniclabs/MethodCache/blob/main/LICENSE).

OpenTelemetry instruments the unkeyed `ICacheManager` registration and leaves unrelated keyed managers untouched. A keyed-only registration does not satisfy its requirement for a default manager.

OpenTelemetry preserves the original cache manager registration lifetime and DI disposal ownership. Type/factory-created managers remain container-owned; supplied instances remain caller-owned, including asynchronous disposal.

An instrumented cache lookup generates the application cache key once and reuses it for tracing and the presence-aware inner lookup, preserving the original method and arguments without a second serialization/hash pass.
