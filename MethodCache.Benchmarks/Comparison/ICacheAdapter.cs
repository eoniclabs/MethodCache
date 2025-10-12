namespace MethodCache.Benchmarks.Comparison;

/// <summary>
/// Common abstraction for all caching libraries to enable fair comparison
/// </summary>
public interface ICacheAdapter : IDisposable
{
    /// <summary>
    /// Name of the caching library (for display)
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Get or create a cached value
    /// </summary>
    Task<TValue> GetOrSetAsync<TValue>(
        string key,
        Func<Task<TValue>> factory,
        TimeSpan duration);

    /// <summary>
    /// Try to get a value from cache
    /// </summary>
    bool TryGet<TValue>(string key, out TValue? value);

    /// <summary>
    /// Set a value in cache
    /// </summary>
    void Set<TValue>(string key, TValue value, TimeSpan duration);

    /// <summary>
    /// Remove a value from cache
    /// </summary>
    void Remove(string key);

    /// <summary>
    /// Clear all cached values
    /// </summary>
    void Clear();

    /// <summary>
    /// Get statistics about cache performance (optional)
    /// </summary>
    CacheStatistics GetStatistics();
}

/// <summary>
/// Statistics about cache performance
/// </summary>
public class CacheStatistics
{
    public long Hits { get; set; }
    public long Misses { get; set; }
    public long FactoryCalls { get; set; }
    public TimeSpan TotalFactoryDuration { get; set; }

    public double HitRate => Hits + Misses > 0 ? (double)Hits / (Hits + Misses) : 0;
    public TimeSpan AverageFactoryDuration => FactoryCalls > 0
        ? TimeSpan.FromTicks(TotalFactoryDuration.Ticks / FactoryCalls)
        : TimeSpan.Zero;

    public void Reset()
    {
        Hits = 0;
        Misses = 0;
        FactoryCalls = 0;
        TotalFactoryDuration = TimeSpan.Zero;
    }
}
