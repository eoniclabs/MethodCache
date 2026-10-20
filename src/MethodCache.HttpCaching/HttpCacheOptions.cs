namespace MethodCache.HttpCaching;

/// <summary>
/// Settings for HTTP caching on one HttpClient. Set them in <c>AddHttpCaching(options => ...)</c>.
/// </summary>
/// <remarks>
/// Only GET and HEAD responses are cached. Other safe methods (OPTIONS, TRACE) go to the origin and change nothing;
/// any other method, including one the cache does not know, goes to the origin and, when it succeeds, removes the
/// cached responses for its URL (RFC 9111 section 4.4). <see cref="MaxResponseBytes"/> and the background
/// revalidation timeout must be positive (the timeout at most 4,294,967,294 milliseconds), and freshness and
/// retention durations non-negative. Named settings are validated when the host starts.
/// </remarks>
public sealed class HttpCacheOptions
{
    /// <summary>
    /// Allows caching with a transport other than SocketsHttpHandler or HttpClientHandler. Default false.
    /// Enable only after verifying that the custom transport and every opaque wrapper send no automatic cookies
    /// and add no caller identity below the cache handler. Known transports with automatic cookies always bypass.
    /// </summary>
    public bool AllowUnverifiedTransport { get; set; }

    /// <summary>Preserves transport cookie configuration and bypasses response caching completely.
    /// Default false disables automatic cookies on known transports after all handler configuration.</summary>
    public bool KeepCookies { get; set; }

    /// <summary>Static application tags for this client, in addition to its escaped http:client tag.
    /// They participate in shared generations and administration; do not derive them from individual requests.
    /// At most 125 nonblank custom tags are supported, each at most 1024 UTF-16 code units.</summary>
    public ISet<string> Tags { get; } = new HashSet<string>(StringComparer.Ordinal);

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

    /// <summary>
    /// Responses with a body larger than this many bytes are not cached. Default 10 MiB. Must be positive; set
    /// <see cref="long.MaxValue"/> for no limit.
    /// </summary>
    public long MaxResponseBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Request headers that are always part of the cache key, for APIs that vary responses on a header
    /// (a tenant id, say) without sending <c>Vary</c>.
    /// </summary>
    public ISet<string> KeyHeaders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Adds this cache's member to the RFC 9211 <c>Cache-Status</c> response header, after any members the origin's
    /// own caches added. Useful while developing. Default <c>false</c>.
    /// </summary>
    /// <remarks>
    /// The member is named <c>MethodCache</c>. A response served from the cache reads <c>MethodCache; hit; ttl=42</c>,
    /// where <c>ttl</c> is the remaining freshness in seconds, negative once stale. A request that went to the origin
    /// reads, for example, <c>MethodCache; fwd=uri-miss; fwd-status=200; stored</c>: <c>fwd</c> says why
    /// (<c>uri-miss</c>, <c>vary-miss</c>, <c>stale</c>, <c>request</c>, <c>method</c> or <c>bypass</c>),
    /// <c>fwd-status</c> is the origin's status and <c>stored</c> that the response was cached. A stale response
    /// carries <c>detail=stale-while-revalidate</c> or <c>detail=stale-if-error</c>. The <c>Age</c> header is set on
    /// every response served from the cache, whatever this option says.
    /// </remarks>
    public bool AddDiagnosticHeaders { get; set; }
}
