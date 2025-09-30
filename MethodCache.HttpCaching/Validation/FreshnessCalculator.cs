using MethodCache.HttpCaching.Options;

namespace MethodCache.HttpCaching.Validation;

/// <summary>
/// Calculates the freshness of cached responses according to RFC 7234.
/// </summary>
public class FreshnessCalculator
{
    private readonly CacheBehaviorOptions _behavior;
    private readonly CacheFreshnessOptions _freshness;

    public FreshnessCalculator(CacheBehaviorOptions behavior, CacheFreshnessOptions freshness)
    {
        _behavior = behavior;
        _freshness = freshness;
    }

    public bool IsFresh(HttpCacheEntry entry)
    {
        if (_behavior.RespectCacheControl && entry.CacheControl?.NoCache == true)
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

    public TimeSpan GetCurrentAge(HttpCacheEntry entry) => CalculateCurrentAge(entry);

    public TimeSpan? GetFreshnessLifetime(HttpCacheEntry entry) => CalculateFreshnessLifetime(entry);

    private TimeSpan? CalculateFreshnessLifetime(HttpCacheEntry entry)
    {
        if (_behavior.RespectCacheControl && entry.CacheControl?.MaxAge.HasValue == true)
        {
            return entry.CacheControl.MaxAge.Value;
        }

        if (_behavior.IsSharedCache && _behavior.RespectCacheControl && entry.CacheControl?.SharedMaxAge.HasValue == true)
        {
            return entry.CacheControl.SharedMaxAge.Value;
        }

        if (entry.Expires.HasValue && entry.Date.HasValue)
        {
            var expiresLifetime = entry.Expires.Value - entry.Date.Value;
            if (expiresLifetime > TimeSpan.Zero)
            {
                return expiresLifetime;
            }
        }

        if (_freshness.AllowHeuristicFreshness && entry.LastModified.HasValue && entry.Date.HasValue)
        {
            var age = entry.Date.Value - entry.LastModified.Value;
            var heuristicFreshness = TimeSpan.FromSeconds(Math.Max(0, age.TotalSeconds * 0.1));

            if (heuristicFreshness > _freshness.MaxHeuristicFreshness)
            {
                heuristicFreshness = _freshness.MaxHeuristicFreshness;
            }

            return heuristicFreshness;
        }

        return _freshness.DefaultMaxAge;
    }

    private static TimeSpan CalculateCurrentAge(HttpCacheEntry entry)
    {
        var now = DateTimeOffset.UtcNow;
        var residentTime = now - entry.StoredAt;

        var responseAge = TimeSpan.Zero;
        if (entry.Date.HasValue)
        {
            responseAge = entry.StoredAt - entry.Date.Value;
            if (responseAge < TimeSpan.Zero)
            {
                responseAge = TimeSpan.Zero;
            }
        }

        return residentTime + responseAge;
    }

    public bool CanUseStaleWhileRevalidate(HttpCacheEntry entry)
    {
        if (!_behavior.EnableStaleWhileRevalidate)
        {
            return false;
        }

        return entry.CacheControl?.Extensions?.Any(e =>
            string.Equals(e.Name, "stale-while-revalidate", StringComparison.OrdinalIgnoreCase)) == true;
    }

    public bool CanUseStaleIfError(HttpCacheEntry entry)
    {
        if (!_behavior.EnableStaleIfError)
        {
            return false;
        }

        var staleness = DateTimeOffset.UtcNow - entry.StoredAt;

        var extension = entry.CacheControl?.Extensions?.FirstOrDefault(e =>
            string.Equals(e.Name, "stale-if-error", StringComparison.OrdinalIgnoreCase));

        if (extension != null)
        {
            if (!string.IsNullOrEmpty(extension.Value) && int.TryParse(extension.Value.Trim('"'), out var timeoutSeconds))
            {
                return staleness < TimeSpan.FromSeconds(timeoutSeconds);
            }

            return true;
        }

        return staleness < _behavior.MaxStaleIfError;
    }
}
