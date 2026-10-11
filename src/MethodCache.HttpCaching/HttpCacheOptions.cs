namespace MethodCache.HttpCaching;

/// <summary>
/// Settings for HTTP caching on one HttpClient. Set them in <c>AddHttpCaching(options => ...)</c>.
/// </summary>
public sealed class HttpCacheOptions
{
    /// <summary>
    /// <c>false</c> (default): a private cache, as in a browser. Responses marked <c>private</c> are cached and the
    /// <c>Authorization</c> header is part of the cache key, so callers never see each other's responses.
    /// <c>true</c>: a shared cache, as in a proxy. <c>private</c> responses are not stored and <c>s-maxage</c> applies.
    /// </summary>
    public bool IsSharedCache { get; set; }

    /// <summary>
    /// Freshness for a response that has an <c>ETag</c> or <c>Last-Modified</c> but no <c>Cache-Control</c> or
    /// <c>Expires</c>. <c>null</c> (default) revalidates such responses with a conditional request every time.
    /// </summary>
    public TimeSpan? DefaultMaxAge { get; set; }

    /// <summary>
    /// Use 10% of the time since <c>Last-Modified</c> as freshness when the server gives none (RFC 9111 section 4.2.2).
    /// Default <c>true</c>, capped by <see cref="MaxHeuristicFreshness"/>.
    /// </summary>
    public bool AllowHeuristicFreshness { get; set; } = true;

    /// <summary>Upper limit for heuristic freshness. Default 1 hour.</summary>
    public TimeSpan MaxHeuristicFreshness { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long an entry is kept after it goes stale, so it can be revalidated with <c>If-None-Match</c> /
    /// <c>If-Modified-Since</c> or served by stale-while-revalidate and stale-if-error. Default 24 hours.
    /// </summary>
    public TimeSpan StaleRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Serve a stale response at once and refresh it in the background when the server sent
    /// <c>stale-while-revalidate</c>. Default <c>false</c>.
    /// </summary>
    public bool EnableStaleWhileRevalidate { get; set; }

    /// <summary>
    /// Serve a stale response when the server cannot be reached. The server's <c>stale-if-error</c> sets the window,
    /// otherwise <see cref="MaxStaleIfError"/>. Default <c>false</c>.
    /// </summary>
    public bool EnableStaleIfError { get; set; }

    /// <summary>How long a response may be stale and still be served on error. Default 24 hours.</summary>
    public TimeSpan MaxStaleIfError { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Timeout for a background revalidation. Default 10 seconds.</summary>
    public TimeSpan BackgroundRevalidationTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Responses larger than this are not cached. Default 10 MB; 0 means no limit.</summary>
    public long MaxResponseSize { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Request headers that are always part of the cache key, for APIs that vary responses on a header
    /// (a tenant id, say) without sending <c>Vary</c>.
    /// </summary>
    public ISet<string> KeyHeaders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Request methods whose responses are cached. Default GET and HEAD. Unsafe methods such as POST are never
    /// cached; a successful one removes the cached responses for its URL instead.
    /// </summary>
    public ISet<HttpMethod> CacheableMethods { get; } = new HashSet<HttpMethod> { HttpMethod.Get, HttpMethod.Head };

    /// <summary>
    /// Adds <c>X-Cache</c> (HIT, MISS, REVALIDATED, STALE, STALE-IF-ERROR), <c>X-Cache-Age</c> and
    /// <c>X-Cache-TTL</c> to responses. Useful while developing. Default <c>false</c>.
    /// </summary>
    public bool AddDiagnosticHeaders { get; set; }
}
