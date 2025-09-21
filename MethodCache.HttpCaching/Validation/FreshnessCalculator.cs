namespace MethodCache.HttpCaching.Validation;

/// <summary>
/// Calculates the freshness of cached responses according to RFC 7234.
/// </summary>
public class FreshnessCalculator
{
    private readonly HttpCacheOptions _options;

    public FreshnessCalculator(HttpCacheOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Determines if a cache entry is fresh according to HTTP caching rules.
    /// </summary>
    public bool IsFresh(HttpCacheEntry entry)
    {
        // If Cache-Control: no-cache is present, always stale
        if (entry.CacheControl?.NoCache == true)
        {
            return false;
        }

        var freshnessLifetime = CalculateFreshnessLifetime(entry);
        if (!freshnessLifetime.HasValue)
        {
            return false;
        }

        var currentAge = CalculateCurrentAge(entry);
        return currentAge < freshnessLifetime.Value;
    }

    /// <summary>
    /// Calculates the freshness lifetime of a cache entry in seconds.
    /// </summary>
    private TimeSpan? CalculateFreshnessLifetime(HttpCacheEntry entry)
    {
        // 1. Check Cache-Control max-age directive (highest priority)
        if (_options.RespectCacheControl && entry.CacheControl?.MaxAge.HasValue == true)
        {
            return entry.CacheControl.MaxAge.Value;
        }

        // 2. Check Cache-Control s-maxage for shared caches
        if (_options.IsSharedCache &&
            _options.RespectCacheControl &&
            entry.CacheControl?.SharedMaxAge.HasValue == true)
        {
            return entry.CacheControl.SharedMaxAge.Value;
        }

        // 3. Check Expires header
        if (entry.Expires.HasValue && entry.Date.HasValue)
        {
            var expiresLifetime = entry.Expires.Value - entry.Date.Value;
            if (expiresLifetime > TimeSpan.Zero)
            {
                return expiresLifetime;
            }
        }

        // 4. Use heuristic freshness if allowed
        if (_options.AllowHeuristicFreshness && entry.LastModified.HasValue && entry.Date.HasValue)
        {
            // RFC 7234 suggests using 10% of the time since last modification
            var age = entry.Date.Value - entry.LastModified.Value;
            var heuristicFreshness = TimeSpan.FromSeconds(age.TotalSeconds * 0.1);

            // Cap heuristic freshness at the configured maximum
            if (heuristicFreshness > _options.MaxHeuristicFreshness)
            {
                heuristicFreshness = _options.MaxHeuristicFreshness;
            }

            return heuristicFreshness;
        }

        // 5. Use default max age if configured
        return _options.DefaultMaxAge;
    }

    /// <summary>
    /// Calculates the current age of a cache entry.
    /// </summary>
    private TimeSpan CalculateCurrentAge(HttpCacheEntry entry)
    {
        // RFC 7234 compliant age calculation
        var now = DateTimeOffset.UtcNow;

        // Age when we received the response (resident_time)
        var residentTime = now - entry.StoredAt;

        // Age based on response Date header (if available)
        var responseAge = TimeSpan.Zero;
        if (entry.Date.HasValue)
        {
            responseAge = entry.StoredAt - entry.Date.Value;
            if (responseAge < TimeSpan.Zero)
                responseAge = TimeSpan.Zero; // Clock skew protection
        }

        // Total age = resident time + response age
        return residentTime + responseAge;
    }

    /// <summary>
    /// Determines if a stale cache entry can be used while revalidating.
    /// </summary>
    public bool CanUseStaleWhileRevalidate(HttpCacheEntry entry)
    {
        if (!_options.EnableStaleWhileRevalidate)
        {
            return false;
        }

        // Check if the response allows stale-while-revalidate
        if (entry.CacheControl?.Extensions?.Any(e =>
            e.Name?.Equals("stale-while-revalidate", StringComparison.OrdinalIgnoreCase) == true) == true)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Determines if a stale cache entry can be used when there's an error.
    /// </summary>
    public bool CanUseStaleIfError(HttpCacheEntry entry)
    {
        if (!_options.EnableStaleIfError)
        {
            return false;
        }

        var staleness = DateTimeOffset.UtcNow - entry.StoredAt;

        // Check if the response allows stale-if-error with specific timeout
        var staleIfErrorExtension = entry.CacheControl?.Extensions?.FirstOrDefault(e =>
            e.Name?.Equals("stale-if-error", StringComparison.OrdinalIgnoreCase) == true);

        if (staleIfErrorExtension != null)
        {
            // If stale-if-error has a value, use it as the timeout in seconds
            if (int.TryParse(staleIfErrorExtension.Value, out var timeoutSeconds))
            {
                return staleness < TimeSpan.FromSeconds(timeoutSeconds);
            }
            // If no value or invalid value, allow stale-if-error without timeout
            return true;
        }

        // Use global stale-if-error timeout
        return staleness < _options.MaxStaleIfError;
    }
}