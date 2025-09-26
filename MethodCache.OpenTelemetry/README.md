# MethodCache.OpenTelemetry

[![NuGet Version](https://img.shields.io/nuget/v/MethodCache.OpenTelemetry)](https://www.nuget.org/packages/MethodCache.OpenTelemetry)

Comprehensive OpenTelemetry instrumentation for MethodCache, providing distributed tracing, metrics collection, and HTTP correlation for cache operations.

## Features

### 🔍 Distributed Tracing
- Automatic span creation for all cache operations
- Parent-child span relationships for nested operations
- Cache hit/miss status tracking
- Key generation timing measurement
- Storage provider operation tracing
- Exception recording with stack traces

### 📊 Metrics Collection
- **Counters**: Cache hits, misses, errors, evictions
- **Histograms**: Operation latency, key generation time, serialization duration
- **Gauges**: Current entries count, memory usage, hit ratio
- **Provider-specific metrics**: Redis, SQL Server, Memory storage

### 🔗 HTTP Correlation
- Automatic correlation with HTTP requests
- Request ID and trace ID propagation
- User and tenant context tracking
- Baggage propagation across service boundaries

## Installation

```bash
dotnet add package MethodCache.OpenTelemetry
```

## Quick Start

### Basic Setup

```csharp
using MethodCache.OpenTelemetry.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add MethodCache with OpenTelemetry
builder.Services.AddMethodCache(config =>
{
    config.DefaultDuration(TimeSpan.FromMinutes(10));
});

// Add OpenTelemetry instrumentation
builder.Services.AddMethodCacheOpenTelemetry(options =>
{
    options.EnableTracing = true;
    options.EnableMetrics = true;
    options.EnableHttpCorrelation = true;
});

// Configure OpenTelemetry SDK
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddMethodCacheInstrumentation()
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMethodCacheInstrumentation()
        .AddPrometheusExporter());

var app = builder.Build();

// Enable HTTP correlation middleware
app.UseMethodCacheHttpCorrelation();

app.Run();
```

### Advanced Configuration

```csharp
builder.Services.AddMethodCacheOpenTelemetry(options =>
{
    // Tracing Configuration
    options.EnableTracing = true;
    options.EnableDistributedTracing = true;
    options.SamplingRatio = 0.1; // Sample 10% of traces

    // Metrics Configuration
    options.EnableMetrics = true;
    options.MetricExportInterval = TimeSpan.FromSeconds(30);

    // Privacy & Security
    options.RecordCacheKeys = true;
    options.HashCacheKeys = true; // Hash keys for privacy
    options.ExportSensitiveData = false;

    // HTTP Correlation
    options.EnableHttpCorrelation = true;
    options.EnableBaggagePropagation = true;

    // Storage Provider Instrumentation
    options.EnableStorageProviderInstrumentation = true;

    // Service Information
    options.ServiceName = "MyApi";
    options.ServiceVersion = "1.0.0";
    options.Environment = "Production";
});
```

## Usage Examples

### Automatic Instrumentation

Once configured, all cache operations are automatically instrumented:

```csharp
public interface IUserService
{
    [Cache(Duration = "00:05:00", Tags = new[] { "user" })]
    Task<User> GetUserAsync(int userId);
}

// This will automatically create spans and metrics:
// - Span: MethodCache.cache.get
// - Metrics: methodcache.hits_total, methodcache.operation_duration
var user = await userService.GetUserAsync(123);
```

### Manual Baggage Propagation

```csharp
public class OrderController : ControllerBase
{
    private readonly IBaggagePropagator _baggagePropagator;

    [HttpPost]
    public async Task<IActionResult> CreateOrder(OrderRequest request)
    {
        // Set correlation ID for distributed tracing
        _baggagePropagator.SetCacheCorrelationId(Guid.NewGuid().ToString());
        _baggagePropagator.SetCacheUserId(User.Identity.Name);

        // These values will propagate to all cache operations
        var order = await _orderService.CreateOrderAsync(request);
        return Ok(order);
    }
}
```

### Custom Metrics Collection

```csharp
public class CustomCacheMetrics
{
    private readonly ICacheMeterProvider _meterProvider;

    public void RecordCustomOperation(string methodName, double duration)
    {
        _meterProvider.RecordOperationDuration(methodName, duration,
            new Dictionary<string, object?>
            {
                ["custom_tag"] = "value",
                ["operation_type"] = "batch"
            });
    }
}
```

## Trace Attributes

Each cache operation span includes these attributes:

| Attribute | Description | Example |
|-----------|-------------|---------|
| `cache.operation` | Operation type | `get`, `set`, `delete` |
| `cache.hit` | Cache hit status | `true`, `false` |
| `cache.method` | Method name | `IUserService.GetUserAsync` |
| `cache.key_hash` | Hashed cache key | `a1b2c3d4` |
| `cache.ttl_seconds` | Time-to-live | `600` |
| `cache.provider` | Storage provider | `redis`, `memory` |
| `cache.tags` | Cache tags | `user,profile` |
| `cache.group` | Cache group | `users` |
| `cache.version` | Cache version | `2` |
| `http.request_id` | HTTP request ID | `abc-123-def` |
| `http.method` | HTTP method | `GET`, `POST` |
| `http.path` | Request path | `/api/users/123` |

## Metrics

### Counter Metrics

| Metric | Description | Unit | Tags |
|--------|-------------|------|------|
| `methodcache.hits_total` | Total cache hits | count | method, group |
| `methodcache.misses_total` | Total cache misses | count | method, group |
| `methodcache.errors_total` | Total errors | count | method, error.type |
| `methodcache.evictions_total` | Total evictions | count | method, eviction.reason |

### Histogram Metrics

| Metric | Description | Unit | Buckets |
|--------|-------------|------|---------|
| `methodcache.operation_duration` | Operation latency | ms | 0.1, 1, 10, 100, 1000 |
| `methodcache.key_generation_duration` | Key generation time | ms | 0.01, 0.1, 1, 10 |
| `methodcache.serialization_duration` | Serialization time | ms | 0.1, 1, 10, 100 |
| `methodcache.storage_operation_duration` | Storage latency | ms | 1, 10, 100, 1000 |

### Gauge Metrics

| Metric | Description | Unit |
|--------|-------------|------|
| `methodcache.entries_count` | Current cache entries | count |
| `methodcache.memory_bytes` | Memory usage | bytes |
| `methodcache.hit_ratio` | Cache hit ratio | ratio |

## Exporters Configuration

### Jaeger

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddMethodCacheInstrumentation()
        .AddJaegerExporter(options =>
        {
            options.AgentHost = "localhost";
            options.AgentPort = 6831;
        }));
```

### Prometheus

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMethodCacheInstrumentation()
        .AddPrometheusExporter());

app.UseOpenTelemetryPrometheusScrapingEndpoint(); // /metrics endpoint
```

### OTLP (OpenTelemetry Protocol)

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddMethodCacheInstrumentation()
        .AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri("http://localhost:4317");
            options.Protocol = OtlpExportProtocol.Grpc;
        }))
    .WithMetrics(metrics => metrics
        .AddMethodCacheInstrumentation()
        .AddOtlpExporter());
```

## Grafana Dashboard

Example Grafana dashboard JSON for MethodCache metrics:

```json
{
  "panels": [
    {
      "title": "Cache Hit Ratio",
      "targets": [{
        "expr": "rate(methodcache_hits_total[5m]) / (rate(methodcache_hits_total[5m]) + rate(methodcache_misses_total[5m]))"
      }]
    },
    {
      "title": "Operation Latency P99",
      "targets": [{
        "expr": "histogram_quantile(0.99, rate(methodcache_operation_duration_bucket[5m]))"
      }]
    },
    {
      "title": "Cache Errors Rate",
      "targets": [{
        "expr": "rate(methodcache_errors_total[5m])"
      }]
    }
  ]
}
```

## Performance Considerations

- **Sampling**: Use `SamplingRatio` to reduce overhead in high-traffic scenarios
- **Key Hashing**: Enable `HashCacheKeys` to protect sensitive data while maintaining observability
- **Batching**: Metrics are batched and exported at `MetricExportInterval`
- **Overhead**: < 1% performance impact with default settings

## Troubleshooting

### No Traces Appearing

1. Verify OpenTelemetry SDK is configured:
```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddMethodCacheInstrumentation());
```

2. Check activity source is registered:
```csharp
TracingConstants.ActivitySource.HasListeners() // Should be true
```

### Missing HTTP Correlation

Ensure middleware is registered:
```csharp
app.UseMethodCacheHttpCorrelation(); // Must be early in pipeline
```

### High Memory Usage

Adjust sampling ratio:
```csharp
options.SamplingRatio = 0.01; // Sample only 1% of traces
```

## Best Practices

1. **Use Sampling in Production**: Set appropriate `SamplingRatio` for your traffic volume
2. **Protect Sensitive Data**: Enable `HashCacheKeys` when cache keys contain PII
3. **Monitor Overhead**: Track the performance impact of telemetry
4. **Use Baggage Sparingly**: Baggage adds overhead to every span
5. **Configure Alerts**: Set up alerts on error rates and latency percentiles

## Contributing

Contributions are welcome! Please read our contributing guidelines and submit pull requests to our repository.

## License

MIT License - see LICENSE file for details.