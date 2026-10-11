namespace MethodCache.HttpCaching.Options;

public class CacheFreshnessOptions
{
    public bool AllowHeuristicFreshness { get; set; } = true;
    public TimeSpan MaxHeuristicFreshness { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Freshness for a response that has an <c>ETag</c> or <c>Last-Modified</c> but no <c>Cache-Control</c>,
    /// <c>Expires</c> or heuristic freshness. <c>null</c> (the default) revalidates such responses on every request.
    /// </summary>
    public TimeSpan? DefaultMaxAge { get; set; }

    /// <summary>
    /// How long an entry is kept after it goes stale, so it can be revalidated with <c>If-None-Match</c> /
    /// <c>If-Modified-Since</c> or served by stale-while-revalidate and stale-if-error. Default 24 hours.
    /// </summary>
    public TimeSpan StaleRetention { get; set; } = TimeSpan.FromHours(24);
}
