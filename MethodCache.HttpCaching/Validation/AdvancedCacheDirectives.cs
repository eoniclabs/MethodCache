using System.Net.Http.Headers;

namespace MethodCache.HttpCaching.Validation;

/// <summary>
/// Handles advanced cache control directives including must-revalidate, proxy-revalidate,
/// immutable, and other RFC 9111 directives.
/// </summary>
public class AdvancedCacheDirectives
{
    /// <summary>
    /// Configuration for handling advanced cache directives.
    /// </summary>
    public class Options
    {
        /// <summary>
        /// Gets or sets whether to respect must-revalidate directive.
        /// When true, stale responses cannot be used without successful revalidation.
        /// </summary>
        public bool RespectMustRevalidate { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to respect proxy-revalidate directive.
        /// Only applies to shared caches.
        /// </summary>
        public bool RespectProxyRevalidate { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to respect the immutable directive.
        /// Immutable responses don't need revalidation during their freshness lifetime.
        /// </summary>
        public bool RespectImmutable { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to respect the stale-while-revalidate directive.
        /// </summary>
        public bool RespectStaleWhileRevalidate { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to respect the stale-if-error directive.
        /// </summary>
        public bool RespectStaleIfError { get; set; } = true;

        /// <summary>
        /// Gets or sets the default min-fresh value for requests.
        /// </summary>
        public TimeSpan? DefaultMinFresh { get; set; }

        /// <summary>
        /// Gets or sets the default max-stale value for requests.
        /// </summary>
        public TimeSpan? DefaultMaxStale { get; set; }
    }

    private readonly Options _options;

    public AdvancedCacheDirectives(Options? options = null)
    {
        _options = options ?? new Options();
    }

    /// <summary>
    /// Parses advanced directives from response headers.
    /// </summary>
    public ResponseDirectives ParseResponse(HttpResponseMessage response)
    {
        var directives = new ResponseDirectives();
        var cacheControl = response.Headers.CacheControl;

        // Parse Cache-Control directives if present
        if (cacheControl != null)
        {
            // Standard directives
            directives.MustRevalidate = cacheControl.MustRevalidate == true;
            directives.ProxyRevalidate = cacheControl.ProxyRevalidate == true;
            directives.NoCache = cacheControl.NoCache == true;
            directives.NoStore = cacheControl.NoStore == true;
            directives.Private = cacheControl.Private == true;
            directives.Public = cacheControl.Public == true;
            directives.NoTransform = cacheControl.NoTransform == true;

            // Parse extensions for immutable, stale-while-revalidate, stale-if-error
            if (cacheControl.Extensions != null)
            {
                foreach (var extension in cacheControl.Extensions)
                {
                    ParseResponseExtension(extension, directives);
                }
            }
        }

        // Parse Surrogate-Control header if present (independent of Cache-Control)
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
                if (!string.IsNullOrEmpty(extension.Value) &&
                    int.TryParse(extension.Value.Trim('"'), out var swrSeconds))
                {
                    directives.StaleWhileRevalidate = TimeSpan.FromSeconds(swrSeconds);
                }
                break;

            case "stale-if-error":
                if (!string.IsNullOrEmpty(extension.Value) &&
                    int.TryParse(extension.Value.Trim('"'), out var sieSeconds))
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
            return;

        var surrogateControl = string.Join(",", values);

        // Parse surrogate-control directives (simplified)
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

    /// <summary>
    /// Determines if a stale response can be used based on directives.
    /// </summary>
    public bool CanUseStaleResponse(
        ResponseDirectives responseDirectives,
        RequestCacheDirectives requestDirectives,
        bool isSharedCache,
        bool isErrorCondition)
    {
        // must-revalidate prevents using stale responses
        if (_options.RespectMustRevalidate && responseDirectives.MustRevalidate)
            return false;

        // proxy-revalidate prevents shared caches from using stale responses
        if (_options.RespectProxyRevalidate && responseDirectives.ProxyRevalidate && isSharedCache)
            return false;

        // Check if stale-if-error allows stale response in error conditions
        if (isErrorCondition && _options.RespectStaleIfError && responseDirectives.StaleIfError.HasValue)
        {
            return true; // Staleness check done elsewhere with the duration
        }

        // Check if stale-while-revalidate allows stale response
        if (!isErrorCondition && _options.RespectStaleWhileRevalidate && responseDirectives.StaleWhileRevalidate.HasValue)
        {
            return true; // Staleness check done elsewhere with the duration
        }

        // Check request directives
        return requestDirectives.AllowsStale();
    }

    /// <summary>
    /// Determines if revalidation is required.
    /// </summary>
    public bool RequiresRevalidation(
        ResponseDirectives responseDirectives,
        RequestCacheDirectives requestDirectives,
        bool isStale,
        bool isSharedCache)
    {
        // no-cache always requires revalidation
        if (responseDirectives.NoCache || requestDirectives.NoCache)
            return true;

        // Fresh responses with immutable don't need revalidation
        if (!isStale && _options.RespectImmutable && responseDirectives.Immutable)
            return false;

        // Stale responses with must-revalidate require revalidation
        if (isStale && _options.RespectMustRevalidate && responseDirectives.MustRevalidate)
            return true;

        // Stale responses with proxy-revalidate require revalidation in shared caches
        if (isStale && isSharedCache && _options.RespectProxyRevalidate && responseDirectives.ProxyRevalidate)
            return true;

        return false;
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

    /// <summary>
    /// Checks if a response is cacheable based on directives.
    /// </summary>
    public bool IsCacheable(ResponseDirectives directives, bool isSharedCache)
    {
        // no-store prevents caching
        if (directives.NoStore)
            return false;

        // Surrogate-Control no-store prevents CDN caching
        if (directives.SurrogateNoStore && isSharedCache)
            return false;

        // private responses cannot be stored in shared caches
        if (directives.Private && isSharedCache)
            return false;

        return true;
    }

    /// <summary>
    /// Calculates the effective max age considering surrogate control.
    /// </summary>
    public TimeSpan? GetEffectiveMaxAge(
        ResponseDirectives directives,
        TimeSpan? standardMaxAge,
        bool isSharedCache)
    {
        // Surrogate-Control max-age overrides Cache-Control max-age for shared caches
        if (isSharedCache && directives.SurrogateMaxAge.HasValue)
        {
            return directives.SurrogateMaxAge;
        }

        return standardMaxAge;
    }
}