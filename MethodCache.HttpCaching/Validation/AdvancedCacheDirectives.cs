using System.Net.Http.Headers;
using MethodCache.HttpCaching.Options;

namespace MethodCache.HttpCaching.Validation;

/// <summary>
/// Handles advanced cache control directives including must-revalidate, proxy-revalidate,
/// immutable, and other RFC 9111 directives.
/// </summary>
public class AdvancedCacheDirectives
{
    private readonly CacheBehaviorOptions _behavior;

    public AdvancedCacheDirectives(CacheBehaviorOptions behavior)
    {
        _behavior = behavior;
    }

    /// <summary>
    /// Response cache directives.
    /// </summary>
    public class ResponseDirectives
    {
        public bool MustRevalidate { get; set; }
        public bool ProxyRevalidate { get; set; }
        public bool NoCache { get; set; }
        public bool NoStore { get; set; }
        public bool Private { get; set; }
        public bool Public { get; set; }
        public bool NoTransform { get; set; }
        public bool Immutable { get; set; }
        public bool MustUnderstand { get; set; }
        public TimeSpan? StaleWhileRevalidate { get; set; }
        public TimeSpan? StaleIfError { get; set; }
        public bool SurrogateNoStore { get; set; }
        public bool SurrogateNoStoreRemote { get; set; }
        public TimeSpan? SurrogateMaxAge { get; set; }
    }

    public ResponseDirectives ParseResponse(HttpResponseMessage response)
    {
        var directives = new ResponseDirectives();
        var cacheControl = response.Headers.CacheControl;

        if (cacheControl != null)
        {
            directives.MustRevalidate = cacheControl.MustRevalidate == true;
            directives.ProxyRevalidate = cacheControl.ProxyRevalidate == true;
            directives.NoCache = cacheControl.NoCache == true;
            directives.NoStore = cacheControl.NoStore == true;
            directives.Private = cacheControl.Private == true;
            directives.Public = cacheControl.Public == true;
            directives.NoTransform = cacheControl.NoTransform == true;

            if (cacheControl.Extensions != null)
            {
                foreach (var extension in cacheControl.Extensions)
                {
                    ParseResponseExtension(extension, directives);
                }
            }
        }

        ParseSurrogateControl(response, directives);
        return directives;
    }

    private void ParseResponseExtension(NameValueHeaderValue extension, ResponseDirectives directives)
    {
        switch (extension.Name.ToLowerInvariant())
        {
            case "immutable":
                directives.Immutable = true;
                break;
            case "stale-while-revalidate":
                if (!string.IsNullOrEmpty(extension.Value) && int.TryParse(extension.Value.Trim('"'), out var swrSeconds))
                {
                    directives.StaleWhileRevalidate = TimeSpan.FromSeconds(swrSeconds);
                }
                break;
            case "stale-if-error":
                if (!string.IsNullOrEmpty(extension.Value) && int.TryParse(extension.Value.Trim('"'), out var sieSeconds))
                {
                    directives.StaleIfError = TimeSpan.FromSeconds(sieSeconds);
                }
                break;
            case "must-understand":
                directives.MustUnderstand = true;
                break;
        }
    }

    private void ParseSurrogateControl(HttpResponseMessage response, ResponseDirectives directives)
    {
        if (!response.Headers.TryGetValues("Surrogate-Control", out var values))
        {
            return;
        }

        var surrogateControl = string.Join(",", values);

        if (surrogateControl.Contains("no-store", StringComparison.OrdinalIgnoreCase))
        {
            directives.SurrogateNoStore = true;
        }

        if (surrogateControl.Contains("no-store-remote", StringComparison.OrdinalIgnoreCase))
        {
            directives.SurrogateNoStoreRemote = true;
        }

        var maxAgeMatch = System.Text.RegularExpressions.Regex.Match(
            surrogateControl, @"max-age\s*=\s*(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (maxAgeMatch.Success && int.TryParse(maxAgeMatch.Groups[1].Value, out var maxAge))
        {
            directives.SurrogateMaxAge = TimeSpan.FromSeconds(maxAge);
        }
    }

    public bool CanUseStaleResponse(
        ResponseDirectives responseDirectives,
        RequestCacheDirectives requestDirectives,
        bool isSharedCache,
        bool isErrorCondition)
    {
        if (_behavior.RespectMustRevalidate && responseDirectives.MustRevalidate)
        {
            return false;
        }

        if (_behavior.RespectProxyRevalidate && responseDirectives.ProxyRevalidate && isSharedCache)
        {
            return false;
        }

        if (isErrorCondition && _behavior.EnableStaleIfError && responseDirectives.StaleIfError.HasValue)
        {
            return true;
        }

        if (!isErrorCondition && _behavior.EnableStaleWhileRevalidate && responseDirectives.StaleWhileRevalidate.HasValue)
        {
            return true;
        }

        return requestDirectives.AllowsStale(_behavior);
    }

    public bool RequiresRevalidation(
        ResponseDirectives responseDirectives,
        RequestCacheDirectives requestDirectives,
        bool isStale,
        bool isSharedCache)
    {
        if (responseDirectives.NoCache || requestDirectives.NoCache)
        {
            return true;
        }

        if (!isStale && _behavior.RespectImmutable && responseDirectives.Immutable)
        {
            return false;
        }

        if (isStale && _behavior.RespectMustRevalidate && responseDirectives.MustRevalidate)
        {
            return true;
        }

        if (isStale && isSharedCache && _behavior.RespectProxyRevalidate && responseDirectives.ProxyRevalidate)
        {
            return true;
        }

        return false;
    }

    public bool IsCacheable(ResponseDirectives directives, bool isSharedCache)
    {
        if (directives.NoStore)
        {
            return false;
        }

        if (directives.SurrogateNoStore && isSharedCache)
        {
            return false;
        }

        if (directives.Private && isSharedCache)
        {
            return false;
        }

        return true;
    }

    public TimeSpan? GetEffectiveMaxAge(
        ResponseDirectives directives,
        TimeSpan? standardMaxAge,
        bool isSharedCache)
    {
        if (isSharedCache && directives.SurrogateMaxAge.HasValue)
        {
            return directives.SurrogateMaxAge;
        }

        return standardMaxAge;
    }
}
