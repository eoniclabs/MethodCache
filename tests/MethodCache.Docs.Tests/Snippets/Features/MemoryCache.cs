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
                o.Weigher = (key, value) => value is string s ? s.Length * 2 : 64;
            }));
            // end-snippet
        }
    }
}
