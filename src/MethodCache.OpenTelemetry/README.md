# MethodCache.OpenTelemetry

[![NuGet Version](https://img.shields.io/nuget/v/MethodCache.OpenTelemetry)](https://www.nuget.org/packages/MethodCache.OpenTelemetry)

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
- **Hits stay allocation-free in the metric producer**: generated decorators answer hits from the cache's fast path, which has no span. The normal method tag and custom lists of up to eight total tags use inline storage; larger custom lists can allocate. SDK processors and exporters have their own costs. Hits still count in `methodcache.hits_total`, and unused instruments skip tag construction.

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

Sampling, export intervals and the service name are set on the OpenTelemetry SDK as for any other library.

## Options

| Option | Default | What it does |
|--------|---------|--------------|
| `EnableTracing` | `true` | Creates spans |
| `EnableMetrics` | `true` | Records metrics |
| `RecordCacheKeys` | `false` | Puts the cache key on spans. Keys can contain argument values |
| `HashCacheKeys` | `true` | With `RecordCacheKeys`, records a hash of the key instead of the key |
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

| Attribute | Description | Example |
|-----------|-------------|---------|
| `cache.method` | Method name | `IUserService.GetUserAsync` |
| `cache.hit` | Whether the value came from the cache | `true` |
| `cache.key` / `cache.key_hash` | The key, with `RecordCacheKeys` | `a1b2c3d4` |
| `cache.ttl_seconds` | Time-to-live | `600` |
| `cache.tags` | Cache tags, with `RecordCacheTags` | `user,profile` |
| `cache.group` | Group | `users` |
| `cache.version` | Version | `2` |
| `cache.keys_count` | Number of keys invalidated | `3` |

## Metrics

Metric dimensions are kept to values with few variants (`method`, `group`, `version`, `error.type`), so a tag such
as `user:42` never becomes a time series of its own.

| Metric | Type | Description | Dimensions |
|--------|------|-------------|------------|
| `methodcache.hits_total` | Counter | Cache hits, including fast-path hits | method |
| `methodcache.misses_total` | Counter | Cache misses | method |
| `methodcache.errors_total` | Counter | Errors (`load_failed` when the cached method throws) | method, error.type |
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

MIT License - see LICENSE file for details.
