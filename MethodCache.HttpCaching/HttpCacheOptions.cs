using MethodCache.HttpCaching.Metrics;
using MethodCache.HttpCaching.Storage;
using MethodCache.HttpCaching.Validation;

namespace MethodCache.HttpCaching;

/// <summary>
/// Configuration options for HTTP caching behavior.
/// </summary>
public class HttpCacheOptions
{
    /// <summary>
    /// Gets or sets whether to respect Cache-Control headers from responses.
    /// Default is true.
    /// </summary>
    public bool RespectCacheControl { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to respect Vary headers for cache key generation.
    /// Default is true.
    /// </summary>
    public bool RespectVary { get; set; } = true;

    /// <summary>
    /// Gets or sets whether this cache should be treated as a shared cache.
    /// Shared caches respect additional directives like s-maxage and don't cache private responses.
    /// Default is false (private cache).
    /// </summary>
    public bool IsSharedCache { get; set; } = false;

    /// <summary>
    /// Gets or sets whether to allow heuristic freshness calculation for responses
    /// without explicit cache directives. Default is true.
    /// </summary>
    public bool AllowHeuristicFreshness { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum heuristic freshness lifetime.
    /// Default is 1 hour.
    /// </summary>
    public TimeSpan MaxHeuristicFreshness { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets or sets the default maximum age for cached responses when no explicit
    /// cache directives are present. Default is 5 minutes.
    /// </summary>
    public TimeSpan? DefaultMaxAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the minimum expiration time for cached responses.
    /// Responses with shorter cache lifetimes will be extended to this minimum.
    /// Default is 30 seconds to balance freshness with performance.
    /// </summary>
    public TimeSpan MinExpiration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the HTTP methods that should be cached.
    /// Default includes GET, HEAD, and OPTIONS.
    /// </summary>
    public HashSet<HttpMethod> CacheableMethods { get; set; } = new()
    {
        HttpMethod.Get,
        HttpMethod.Head,
        HttpMethod.Options
    };

    /// <summary>
    /// Gets or sets the cache storage implementation.
    /// If null, a default in-memory storage will be used.
    /// </summary>
    public IHttpCacheStorage? Storage { get; set; }

    /// <summary>
    /// Gets or sets the maximum size of the cache in bytes.
    /// Default is 100 MB. Set to 0 to disable size limits.
    /// </summary>
    public long MaxCacheSize { get; set; } = 100 * 1024 * 1024; // 100 MB

    /// <summary>
    /// Gets or sets the maximum size of individual responses to cache in bytes.
    /// Default is 10 MB. Responses larger than this will not be cached.
    /// </summary>
    public long MaxResponseSize { get; set; } = 10 * 1024 * 1024; // 10 MB

    /// <summary>
    /// Gets or sets whether to enable stale-while-revalidate behavior.
    /// When enabled, stale cache entries can be served while a background
    /// request revalidates the content. Default is false.
    /// </summary>
    public bool EnableStaleWhileRevalidate { get; set; } = false;

    /// <summary>
    /// Gets or sets whether to enable stale-if-error behavior.
    /// When enabled, stale cache entries can be served if the origin
    /// server returns an error. Default is false.
    /// </summary>
    public bool EnableStaleIfError { get; set; } = false;

    /// <summary>
    /// Gets or sets the maximum time a stale response can be used in error scenarios.
    /// Default is 24 hours.
    /// </summary>
    public TimeSpan MaxStaleIfError { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Gets or sets whether to add diagnostic headers to responses indicating
    /// cache status. Useful for debugging. Default is false.
    /// </summary>
    public bool AddDiagnosticHeaders { get; set; } = false;

    /// <summary>
    /// Gets or sets a function to generate custom cache keys.
    /// If null, a default implementation will be used.
    /// </summary>
    public Func<HttpRequestMessage, string>? CacheKeyGenerator { get; set; }

    /// <summary>
    /// Gets or sets headers that should be included in cache key generation
    /// beyond the standard Vary headers. This is useful for custom caching scenarios.
    /// </summary>
    public string[] AdditionalVaryHeaders { get; set; } = Array.Empty<string>();

    // === NEW ADVANCED FEATURES ===

    /// <summary>
    /// Gets or sets whether to respect must-revalidate directive.
    /// When true, stale responses cannot be used without successful revalidation.
    /// Default is true.
    /// </summary>
    public bool RespectMustRevalidate { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to respect proxy-revalidate directive.
    /// Only applies to shared caches. Default is true.
    /// </summary>
    public bool RespectProxyRevalidate { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to respect the immutable directive.
    /// Immutable responses don't need revalidation during their freshness lifetime.
    /// Default is true.
    /// </summary>
    public bool RespectImmutable { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to respect request max-age directive.
    /// Client specifies maximum acceptable age. Default is true.
    /// </summary>
    public bool RespectRequestMaxAge { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to respect request max-stale directive.
    /// Client indicates willingness to accept stale responses. Default is true.
    /// </summary>
    public bool RespectRequestMaxStale { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to respect request min-fresh directive.
    /// Client wants response to be fresh for at least this long. Default is true.
    /// </summary>
    public bool RespectRequestMinFresh { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to respect only-if-cached directive.
    /// Client only wants cached responses, not from origin. Default is true.
    /// </summary>
    public bool RespectOnlyIfCached { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to generate Warning headers for stale responses
    /// and other cache conditions according to RFC 9111. Default is false.
    /// </summary>
    public bool EnableWarningHeaders { get; set; } = false;

    /// <summary>
    /// Gets or sets whether to enable quality value parsing for Accept headers.
    /// Allows proper content negotiation based on client preferences. Default is true.
    /// </summary>
    public bool EnableQualityValues { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to enable Accept-Language negotiation.
    /// Caches different language variants separately. Default is true.
    /// </summary>
    public bool EnableAcceptLanguage { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to enable Accept-Encoding negotiation.
    /// Handles compression and encoding preferences. Default is true.
    /// </summary>
    public bool EnableAcceptEncoding { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to enable Accept-Charset negotiation.
    /// Default is false (rarely used in modern applications).
    /// </summary>
    public bool EnableAcceptCharset { get; set; } = false;

    /// <summary>
    /// Gets or sets whether to store multiple variants per URL.
    /// Enables content negotiation caching. Default is true.
    /// </summary>
    public bool EnableMultipleVariants { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of variants to cache per URL.
    /// Prevents unbounded variant storage. Default is 5.
    /// </summary>
    public int MaxVariantsPerUrl { get; set; } = 5;

    /// <summary>
    /// Gets or sets whether to enable metrics collection.
    /// Provides hit rates, response times, and other statistics. Default is true.
    /// </summary>
    public bool EnableMetrics { get; set; } = true;

    /// <summary>
    /// Gets or sets the metrics collector instance.
    /// If null, a default implementation will be created.
    /// </summary>
    public IHttpCacheMetrics? Metrics { get; set; }

    /// <summary>
    /// Gets the advanced cache directives options.
    /// </summary>
    public AdvancedCacheDirectives.Options AdvancedDirectives { get; set; } = new();

    /// <summary>
    /// Gets the content negotiation options.
    /// </summary>
    public ContentNegotiationHandler.Options ContentNegotiation { get; set; } = new();
}