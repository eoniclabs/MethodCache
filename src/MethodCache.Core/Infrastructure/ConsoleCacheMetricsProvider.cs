using MethodCache.Extensibility;

namespace MethodCache.Core.Infrastructure
{
    internal sealed class ConsoleCacheMetricsProvider : ICacheMetricsProvider
    {
        public void CacheHit(string methodName)
        {
            Console.WriteLine($"Cache Hit: {methodName}");
        }

        public void CacheMiss(string methodName)
        {
            Console.WriteLine($"Cache Miss: {methodName}");
        }

        public void CacheError(string methodName, string errorMessage)
        {
            Console.WriteLine($"Cache Error: {methodName} - {errorMessage}");
        }

        public void CacheLatency(string methodName, TimeSpan elapsed)
        {
            Console.WriteLine($"Cache Latency: {methodName} - {elapsed.TotalMilliseconds}ms");
        }
    }
}
