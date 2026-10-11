using MethodCache.HttpCaching.Options;

namespace MethodCache.HttpCaching.Validation;

/// <summary>
/// Calculates the freshness of cached responses according to RFC 9111.
/// </summary>
internal sealed class FreshnessCalculator
{
    private readonly CacheBehaviorOptions _behavior;
    private readonly CacheFreshnessOptions _freshness;
    private readonly TimeProvider _timeProvider;

    public FreshnessCalculator(CacheBehaviorOptions behavior, CacheFreshnessOptions freshness, TimeProvider timeProvider)
    {
        _behavior = behavior;
        _freshness = freshness;
        _timeProvider = timeProvider;
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
        if (_behavior.IsSharedCache && _behavior.RespectCacheControl && entry.CacheControl?.SharedMaxAge.HasValue == true)
        {
            return entry.CacheControl.SharedMaxAge.Value;
        }

        if (_behavior.RespectCacheControl && entry.CacheControl?.MaxAge.HasValue == true)
        {
            return entry.CacheControl.MaxAge.Value;
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

    private TimeSpan CalculateCurrentAge(HttpCacheEntry entry)
    {
        var residentTime = _timeProvider.GetUtcNow() - entry.StoredAt;
        if (residentTime < TimeSpan.Zero)
        {
            residentTime = TimeSpan.Zero;
        }

        return entry.InitialAge + residentTime;
    }

    /// <summary>
    /// How long the entry has been stale; zero while it is still fresh.
    /// </summary>
    public TimeSpan GetStaleness(HttpCacheEntry entry)
    {
        var staleness = CalculateCurrentAge(entry) - (CalculateFreshnessLifetime(entry) ?? TimeSpan.Zero);
        return staleness > TimeSpan.Zero ? staleness : TimeSpan.Zero;
    }

    public bool CanUseStaleWhileRevalidate(HttpCacheEntry entry)
    {
        if (!_behavior.EnableStaleWhileRevalidate)
        {
            return false;
        }

        var window = GetExtensionSeconds(entry, "stale-while-revalidate");
        return window.HasValue && GetStaleness(entry) < window.Value;
    }

    public bool CanUseStaleIfError(HttpCacheEntry entry)
    {
        if (!_behavior.EnableStaleIfError)
        {
            return false;
        }

        var window = GetExtensionSeconds(entry, "stale-if-error") ?? _behavior.MaxStaleIfError;
        return GetStaleness(entry) < window;
    }

    private static TimeSpan? GetExtensionSeconds(HttpCacheEntry entry, string name)
    {
        var extension = entry.CacheControl?.Extensions?.FirstOrDefault(e =>
            string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

        if (extension == null)
        {
            return null;
        }

        return int.TryParse(extension.Value?.Trim('"'), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }
}
