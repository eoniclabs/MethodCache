namespace MethodCache.Extensibility
{
    /// <summary>
    /// Receives cache events for monitoring. Each cached call is reported exactly once, as either a hit or a miss.
    /// </summary>
    /// <remarks>
    /// Generated decorators report hits they serve directly from the in-memory fast path. Every other hit and
    /// miss is reported by the <see cref="ICacheManager"/> that handles the call, so a custom cache
    /// manager must report its own hits and misses.
    /// </remarks>
    public interface ICacheMetricsProvider
    {
        /// <summary>A cached value was returned without running the method.</summary>
        void CacheHit(string methodName);

        /// <summary>No cached value existed, so the method runs to produce one.</summary>
        void CacheMiss(string methodName);

        /// <summary>The method failed while loading or refreshing a cached value.</summary>
        void CacheError(string methodName, string errorMessage);

        /// <summary>How long the method took to produce a value after a miss or during a background refresh.</summary>
        void CacheLatency(string methodName, long elapsedMilliseconds);
    }
}
