namespace MethodCache.HttpCaching.Internal;

/// <summary>
/// The request's own <c>Cache-Control</c> directives (RFC 9111 section 5.2.1).
/// </summary>
internal readonly struct RequestCacheDirectives
{
    public bool NoCache { get; private init; }
    public bool NoStore { get; private init; }
    public bool OnlyIfCached { get; private init; }
    public TimeSpan? MaxAge { get; private init; }
    public TimeSpan? MaxStale { get; private init; }
    public TimeSpan? MinFresh { get; private init; }

    public static RequestCacheDirectives Parse(HttpRequestMessage request)
    {
        // The typed accessor allocates the general header view even when the request has no Cache-Control
        if (!request.Headers.NonValidated.Contains("Cache-Control") || request.Headers.CacheControl is not { } cacheControl)
        {
            return default;
        }

        return new RequestCacheDirectives
        {
            NoCache = cacheControl.NoCache,
            NoStore = cacheControl.NoStore,
            OnlyIfCached = cacheControl.OnlyIfCached,
            MaxAge = cacheControl.MaxAge,
            MaxStale = cacheControl.MaxStale ? cacheControl.MaxStaleLimit ?? TimeSpan.MaxValue : null,
            MinFresh = cacheControl.MinFresh
        };
    }

    /// <summary>
    /// Whether a cached response of this age and freshness lifetime satisfies the request's max-age, min-fresh and
    /// max-stale. <paramref name="staleAllowed"/> is false when the response forbids serving it stale.
    /// </summary>
    public bool Accepts(TimeSpan currentAge, TimeSpan freshnessLifetime, bool staleAllowed)
    {
        if (MaxAge is { } maxAge && currentAge > maxAge)
        {
            return false;
        }

        var remaining = freshnessLifetime - currentAge;
        if (MinFresh is { } minFresh && remaining < minFresh)
        {
            return false;
        }

        if (remaining > TimeSpan.Zero)
        {
            return true;
        }

        return staleAllowed && MaxStale is { } maxStale && remaining.Negate() <= maxStale;
    }

    /// <summary>The server's stale extensions cannot override the caller's explicit freshness limits.</summary>
    public bool AcceptsStale(TimeSpan currentAge, TimeSpan freshnessLifetime)
        => !NoCache && (MaxAge == null || currentAge <= MaxAge) &&
           (MinFresh == null || freshnessLifetime - currentAge >= MinFresh) &&
           (MaxStale == null || currentAge - freshnessLifetime <= MaxStale);
}
