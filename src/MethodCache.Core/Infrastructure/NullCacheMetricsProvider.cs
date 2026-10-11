namespace MethodCache.Extensibility
{
    /// <summary>
    /// No-op implementation of <see cref="ICacheMetricsProvider"/> used when metrics tracking
    /// is not explicitly configured. Avoids emitting console logs on hot cache paths.
    /// </summary>
    public sealed class NullCacheMetricsProvider : ICacheMetricsProvider
    {
        /// <inheritdoc />
        public void CacheHit(string methodName) { }
        /// <inheritdoc />
        public void CacheMiss(string methodName) { }
        /// <inheritdoc />
        public void CacheError(string methodName, string errorMessage) { }
        /// <inheritdoc />
        public void CacheLatency(string methodName, long elapsedMilliseconds) { }
    }
}
