using System;

namespace MethodCache.Core
{
    public class ConsoleCacheMetricsProvider : ICacheMetricsProvider
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

        public void CacheLatency(string methodName, long elapsedMilliseconds)
        {
            Console.WriteLine($"Cache Latency: {methodName} - {elapsedMilliseconds}ms");
        }
    }
}
