namespace MethodCache.Core.Infrastructure
{
    /// <summary>
    /// No-op implementation of <see cref="ICacheMetricsProvider"/> used when metrics tracking
    /// is not explicitly configured. Avoids emitting console logs on hot cache paths.
    /// </summary>
    public sealed class NullCacheMetricsProvider : ICacheMetricsProvider
    {
        public void CacheHit(string methodName) { }
        public void CacheMiss(string methodName) { }
        public void CacheError(string methodName, string errorMessage) { }
        public void CacheLatency(string methodName, long elapsedMilliseconds) { }
    }
}
