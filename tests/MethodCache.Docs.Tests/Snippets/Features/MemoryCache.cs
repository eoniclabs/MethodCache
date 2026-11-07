using MethodCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Docs.MemoryCache
{
    public static class Setup
    {
        public static void Options(WebApplicationBuilder builder)
        {
            // begin-snippet: memory-options
            builder.Services.AddMethodCache(cache => cache
                .Defaults(o => o.WithDuration(TimeSpan.FromMinutes(10)))
                .Memory(o =>
                {
                    o.MaxItems = 50_000;                              // default 10,000
                    o.JitterMaxDuration = TimeSpan.FromSeconds(30);   // spread out entries created together
                    o.CacheNullValues = false;                        // a null result means "try again next time"
                }));
            // end-snippet
        }

        public static void Weigher(WebApplicationBuilder builder)
        {
            // begin-snippet: memory-weigher
            builder.Services.AddMethodCache(cache => cache.Memory(o =>
            {
                o.WeightLimit = 256 * 1024 * 1024;   // bound the cache by total size as well as by count
                o.Weigher = MethodCacheWeigher.Bytes; // strings and arrays such as float[] by size, other values 64
            }));
            // end-snippet
        }

        public sealed record Report(IReadOnlyList<string> Rows);

        public static void OwnWeigher(WebApplicationBuilder builder)
        {
            // begin-snippet: memory-own-weigher
            builder.Services.AddMethodCache(cache => cache.Memory(o =>
            {
                o.WeightLimit = 256 * 1024 * 1024;
                // Weigh your own types, and let MethodCacheWeigher weigh the rest
                o.Weigher = (key, value) => value is Report report
                    ? report.Rows.Sum(row => row.Length * 2L)
                    : MethodCacheWeigher.Bytes(key, value);
            }));
            // end-snippet
        }
    }
}
