using System.Net.Http.Headers;

namespace MethodCache.HttpCaching.Validation;

/// <summary>
/// Parses and validates cache directives from HTTP request headers according to RFC 9111.
/// </summary>
public class RequestCacheDirectives
{
    /// <summary>
    /// Gets whether the no-cache directive is present.
    /// Forces revalidation with the origin server.
    /// </summary>
    public bool NoCache { get; private set; }

    /// <summary>
    /// Gets whether the no-store directive is present.
    /// Prevents the cache from storing the response.
    /// </summary>
    public bool NoStore { get; private set; }

    /// <summary>
    /// Gets the max-age directive value if present.
    /// Client is unwilling to accept a response whose age is greater than this.
    /// </summary>
    public TimeSpan? MaxAge { get; private set; }

    /// <summary>
    /// Gets the max-stale directive value if present.
    /// Client is willing to accept a response that has exceeded its expiration time by this amount.
    /// </summary>
    public TimeSpan? MaxStale { get; private set; }

    /// <summary>
    /// Gets the min-fresh directive value if present.
    /// Client wants a response that will still be fresh for at least this long.
    /// </summary>
    public TimeSpan? MinFresh { get; private set; }

    /// <summary>
    /// Gets whether the no-transform directive is present.
    /// Proxies must not transform the response.
    /// </summary>
    public bool NoTransform { get; private set; }

    /// <summary>
    /// Gets whether the only-if-cached directive is present.
    /// Client only wants a response from cache, not from origin.
    /// </summary>
    public bool OnlyIfCached { get; private set; }

    /// <summary>
    /// Gets whether the must-understand directive is present (RFC 9111).
    /// Cache must understand all cache directives or not use cached response.
    /// </summary>
    public bool MustUnderstand { get; private set; }

    /// <summary>
    /// Parses cache directives from a request.
    /// </summary>
    /// <param name="request">The HTTP request message.</param>
    /// <returns>The parsed cache directives.</returns>
    public static RequestCacheDirectives Parse(HttpRequestMessage request)
    {
        var directives = new RequestCacheDirectives();

        if (request.Headers.CacheControl == null)
            return directives;

        var cacheControl = request.Headers.CacheControl;

        directives.NoCache = cacheControl.NoCache == true;
        directives.NoStore = cacheControl.NoStore == true;
        directives.NoTransform = cacheControl.NoTransform == true;
        directives.OnlyIfCached = cacheControl.OnlyIfCached == true;

        if (cacheControl.MaxAge.HasValue)
        {
            directives.MaxAge = cacheControl.MaxAge.Value;
        }

        if (cacheControl.MaxStale == true)
        {
            // Max-stale with no value means willing to accept any staleness
            directives.MaxStale = TimeSpan.MaxValue;
        }
        else if (cacheControl.MaxStaleLimit.HasValue)
        {
            directives.MaxStale = cacheControl.MaxStaleLimit.Value;
        }

        if (cacheControl.MinFresh.HasValue)
        {
            directives.MinFresh = cacheControl.MinFresh.Value;
        }

        // Parse custom extensions like must-understand
        ParseExtensions(cacheControl, directives);

        return directives;
    }

    private static void ParseExtensions(CacheControlHeaderValue cacheControl, RequestCacheDirectives directives)
    {
        if (cacheControl.Extensions == null)
            return;

        foreach (var extension in cacheControl.Extensions)
        {
            if (extension.Name.Equals("must-understand", StringComparison.OrdinalIgnoreCase))
            {
                directives.MustUnderstand = true;
            }
        }
    }

    /// <summary>
    /// Determines if a cached response satisfies the request directives.
    /// </summary>
    /// <param name="entry">The cached entry to validate.</param>
    /// <param name="currentAge">The current age of the cached response.</param>
    /// <param name="freshnessLifetime">The freshness lifetime of the response.</param>
    /// <returns>True if the cached response satisfies the directives.</returns>
    public bool IsSatisfiedBy(HttpCacheEntry entry, TimeSpan currentAge, TimeSpan freshnessLifetime)
    {
        // no-cache always requires revalidation
        if (NoCache)
            return false;

        // no-store should bypass cache entirely (handled elsewhere)
        if (NoStore)
            return false;

        // Check max-age constraint
        if (MaxAge.HasValue && currentAge > MaxAge.Value)
            return false;

        // Check min-fresh constraint
        if (MinFresh.HasValue)
        {
            var remainingLifetime = freshnessLifetime - currentAge;
            if (remainingLifetime < MinFresh.Value)
                return false;
        }

        // Check max-stale constraint
        if (!MaxStale.HasValue)
        {
            // No max-stale means don't accept stale responses
            return currentAge <= freshnessLifetime;
        }
        else if (MaxStale.Value == TimeSpan.MaxValue)
        {
            // max-stale with no value means accept any staleness
            return true;
        }
        else
        {
            // max-stale with value means accept stale up to that amount
            var staleTime = currentAge - freshnessLifetime;
            return staleTime <= MaxStale.Value;
        }
    }

    /// <summary>
    /// Determines if the request requires a cache-only response.
    /// </summary>
    /// <returns>True if only cached responses should be returned.</returns>
    public bool RequiresCacheOnly()
    {
        return OnlyIfCached;
    }

    /// <summary>
    /// Determines if the request allows serving stale responses.
    /// </summary>
    /// <returns>True if stale responses are allowed.</returns>
    public bool AllowsStale()
    {
        return MaxStale.HasValue;
    }

    /// <summary>
    /// Gets the maximum acceptable staleness duration.
    /// </summary>
    /// <returns>The maximum staleness duration, or null if stale responses are not allowed.</returns>
    public TimeSpan? GetMaxStaleness()
    {
        return MaxStale;
    }

    /// <summary>
    /// Creates request directives that bypass the cache.
    /// </summary>
    /// <returns>Directives that force cache bypass.</returns>
    public static RequestCacheDirectives BypassCache()
    {
        return new RequestCacheDirectives
        {
            NoCache = true,
            NoStore = true
        };
    }

    /// <summary>
    /// Creates request directives for cache-only mode.
    /// </summary>
    /// <returns>Directives for cache-only responses.</returns>
    public static RequestCacheDirectives CacheOnly()
    {
        return new RequestCacheDirectives
        {
            OnlyIfCached = true
        };
    }
}