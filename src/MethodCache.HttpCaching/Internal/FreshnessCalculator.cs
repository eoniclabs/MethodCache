using System.Globalization;

namespace MethodCache.HttpCaching.Internal;

/// <summary>
/// Calculates the freshness of cached responses according to RFC 9111 section 4.2.
/// </summary>
internal sealed class FreshnessCalculator
{
    private readonly HttpCacheOptions _options;
    private readonly TimeProvider _timeProvider;

    public FreshnessCalculator(HttpCacheOptions options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
    }

    public bool IsFresh(HttpCacheEntry entry)
    {
        if (entry.CacheControl?.NoCache == true)
        {
            return false;
        }

        return GetFreshnessLifetime(entry) is { } lifetime && GetCurrentAge(entry) < lifetime;
    }

    public TimeSpan? GetFreshnessLifetime(HttpCacheEntry entry)
    {
        if (_options.IsSharedCache && entry.CacheControl?.SharedMaxAge is { } sharedMaxAge)
        {
            return sharedMaxAge;
        }

        if (entry.CacheControl?.MaxAge is { } maxAge)
        {
            return maxAge;
        }

        if (entry.Expires is { } expires && entry.Date is { } date)
        {
            return expires > date ? expires - date : TimeSpan.Zero;
        }

        if (entry.Expires is null && entry.ContentHeaders.Keys.Any(name => string.Equals(name, "Expires", StringComparison.OrdinalIgnoreCase)))
        {
            return TimeSpan.Zero;
        }

        if (_options.AllowHeuristicFreshness && entry.LastModified is { } lastModified && entry.Date is { } responseDate)
        {
            var heuristic = TimeSpan.FromSeconds(Math.Max(0, (responseDate - lastModified).TotalSeconds * 0.1));
            return heuristic > _options.MaxHeuristicFreshness ? _options.MaxHeuristicFreshness : heuristic;
        }

        return _options.DefaultMaxAge;
    }

    public TimeSpan GetCurrentAge(HttpCacheEntry entry)
    {
        var residentTime = _timeProvider.GetUtcNow() - entry.StoredAt;
        residentTime = residentTime > TimeSpan.Zero ? residentTime : TimeSpan.Zero;
        return residentTime > TimeSpan.MaxValue - entry.InitialAge ? TimeSpan.MaxValue : entry.InitialAge + residentTime;
    }

    /// <summary>How long the entry has been stale; zero while it is still fresh.</summary>
    public TimeSpan GetStaleness(HttpCacheEntry entry)
    {
        var staleness = GetCurrentAge(entry) - (GetFreshnessLifetime(entry) ?? TimeSpan.Zero);
        return staleness > TimeSpan.Zero ? staleness : TimeSpan.Zero;
    }

    /// <summary>
    /// <c>no-cache</c>, <c>must-revalidate</c>, and for a shared cache <c>proxy-revalidate</c> and <c>s-maxage</c>, forbid serving the
    /// response stale (RFC 9111 sections 5.2.2.2, 5.2.2.8 and 5.2.2.10).
    /// </summary>
    public bool MayServeStale(HttpCacheEntry entry)
    {
        var cacheControl = entry.CacheControl;
        if (cacheControl == null)
        {
            return true;
        }

        if (cacheControl.NoCache || cacheControl.MustRevalidate)
        {
            return false;
        }

        return !_options.IsSharedCache || (!cacheControl.ProxyRevalidate && cacheControl.SharedMaxAge == null);
    }

    public bool CanUseStaleWhileRevalidate(HttpCacheEntry entry)
    {
        if (!_options.EnableStaleWhileRevalidate || !MayServeStale(entry))
        {
            return false;
        }

        var window = GetExtensionSeconds(entry, "stale-while-revalidate");
        return window.HasValue && GetStaleness(entry) < window.Value;
    }

    public bool CanUseStaleIfError(HttpCacheEntry entry)
    {
        if (!_options.EnableStaleIfError || !MayServeStale(entry))
        {
            return false;
        }

        var window = GetExtensionSeconds(entry, "stale-if-error") ?? _options.MaxStaleIfError;
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

        return int.TryParse(extension.Value?.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }
}
