namespace MethodCache.Core
{
    public interface ICacheMetricsProvider
    {
        void CacheHit(string methodName);
        void CacheMiss(string methodName);
        void CacheError(string methodName, string errorMessage);
        void CacheLatency(string methodName, long elapsedMilliseconds);
    }
}
