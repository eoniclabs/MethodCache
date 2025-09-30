using System.Net.Http;
using MethodCache.HttpCaching.Options;

namespace MethodCache.HttpCaching;

/// <summary>
/// Aggregates configuration for the HTTP caching handler.
/// </summary>
public class HttpCacheOptions
{
    public CacheBehaviorOptions Behavior { get; } = new();
    public CacheFreshnessOptions Freshness { get; } = new();
    public CacheVariationOptions Variation { get; } = new();
    public CacheDiagnosticsOptions Diagnostics { get; } = new();
    public CacheMetricsOptions Metrics { get; } = new();
    public HttpCacheStorageOptions Storage { get; } = new();

    public HashSet<HttpMethod> CacheableMethods { get; } = new()
    {
        HttpMethod.Get,
        HttpMethod.Head,
        HttpMethod.Options
    };

    public Func<HttpRequestMessage, string>? CacheKeyGenerator { get; set; }
}
