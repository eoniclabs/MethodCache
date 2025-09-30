using System.Net.Http.Headers;
using MethodCache.HttpCaching.Options;

namespace MethodCache.HttpCaching.Validation;

/// <summary>
/// Parses and validates cache directives from HTTP request headers according to RFC 9111.
/// </summary>
public class RequestCacheDirectives
{
    public bool NoCache { get; private set; }
    public bool NoStore { get; private set; }
    public TimeSpan? MaxAge { get; private set; }
    public TimeSpan? MaxStale { get; private set; }
    public TimeSpan? MinFresh { get; private set; }
    public bool NoTransform { get; private set; }
    public bool OnlyIfCached { get; private set; }
    public bool MustUnderstand { get; private set; }

    public static RequestCacheDirectives Parse(HttpRequestMessage request)
    {
        var directives = new RequestCacheDirectives();

        if (request.Headers.CacheControl == null)
        {
            return directives;
        }

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

        ParseExtensions(cacheControl, directives);
        return directives;
    }

    private static void ParseExtensions(CacheControlHeaderValue cacheControl, RequestCacheDirectives directives)
    {
        if (cacheControl.Extensions == null)
        {
            return;
        }

        foreach (var extension in cacheControl.Extensions)
        {
            if (extension.Name.Equals("must-understand", StringComparison.OrdinalIgnoreCase))
            {
                directives.MustUnderstand = true;
            }
        }
    }

    public bool IsSatisfiedBy(HttpCacheEntry entry, TimeSpan currentAge, TimeSpan freshnessLifetime, CacheBehaviorOptions behavior)
    {
        if (NoCache)
        {
            return false;
        }

        if (NoStore)
        {
            return false;
        }

        if (behavior.RespectRequestMaxAge && MaxAge.HasValue && currentAge > MaxAge.Value)
        {
            return false;
        }

        if (behavior.RespectRequestMinFresh && MinFresh.HasValue)
        {
            var remainingLifetime = freshnessLifetime - currentAge;
            if (remainingLifetime < MinFresh.Value)
            {
                return false;
            }
        }

        if (!AllowsStale(behavior))
        {
            return currentAge <= freshnessLifetime;
        }

        if (MaxStale == TimeSpan.MaxValue)
        {
            return true;
        }

        var staleTime = currentAge - freshnessLifetime;
        return staleTime <= (MaxStale ?? TimeSpan.Zero);
    }

    public bool RequiresCacheOnly(CacheBehaviorOptions behavior)
    {
        return behavior.RespectOnlyIfCached && OnlyIfCached;
    }

    public bool AllowsStale(CacheBehaviorOptions behavior)
    {
        return behavior.RespectRequestMaxStale && MaxStale.HasValue;
    }

    public TimeSpan? GetMaxStaleness(CacheBehaviorOptions behavior)
    {
        return behavior.RespectRequestMaxStale ? MaxStale : null;
    }

    public static RequestCacheDirectives BypassCache()
    {
        return new RequestCacheDirectives
        {
            NoCache = true,
            NoStore = true
        };
    }
}
