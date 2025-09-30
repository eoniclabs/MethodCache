namespace MethodCache.HttpCaching.Options;

public class CacheFreshnessOptions
{
    public bool AllowHeuristicFreshness { get; set; } = true;
    public TimeSpan MaxHeuristicFreshness { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan? DefaultMaxAge { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan MinExpiration { get; set; } = TimeSpan.FromSeconds(30);
}
