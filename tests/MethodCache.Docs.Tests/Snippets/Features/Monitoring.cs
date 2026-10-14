using MethodCache;
using MethodCache.Extensibility;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Docs.Monitoring
{
    public static class Setup
    {
        public static async Task Administrator(ICacheAdministrator admin)
        {
            // begin-snippet: monitoring-administrator
            var stats = await admin.GetStatsAsync();
            Console.WriteLine($"Hit ratio {stats.HitRatio:P0}, {stats.Entries} entries");

            foreach (var method in admin.GetMethods())
            {
                Console.WriteLine($"{method.MethodId}: {method.Duration}");
            }

            var entries = admin.ListEntries(tag: "user:42");
            await admin.InvalidateTagsAsync(["user:42"]);
            // end-snippet
        }

        public static void ConsoleMetrics(WebApplicationBuilder builder)
        {
            // begin-snippet: monitoring-console-metrics
            builder.Services.AddMethodCache();
            builder.Services.AddConsoleCacheMetrics();   // "Cache Hit: ...", "Cache Miss: ..." on standard output
            // end-snippet
        }

        public static void CustomMetrics(WebApplicationBuilder builder)
        {
            // begin-snippet: monitoring-metrics-provider-registration
            builder.Services.AddMethodCache();
            builder.Services.AddSingleton<ICacheMetricsProvider, CountingMetrics>();
            // end-snippet
        }
    }

    // begin-snippet: monitoring-metrics-provider
    public sealed class CountingMetrics : ICacheMetricsProvider
    {
        private long _hits, _misses, _errors;

        public long Hits => Interlocked.Read(ref _hits);
        public long Misses => Interlocked.Read(ref _misses);
        public long Errors => Interlocked.Read(ref _errors);

        public void CacheHit(string methodName) => Interlocked.Increment(ref _hits);
        public void CacheMiss(string methodName) => Interlocked.Increment(ref _misses);
        public void CacheError(string methodName, string errorMessage) => Interlocked.Increment(ref _errors);
        public void CacheLatency(string methodName, TimeSpan elapsed) { }   // load time after a miss
    }
    // end-snippet
}
