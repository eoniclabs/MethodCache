using MethodCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using FxVendor;

// begin-snippet: readme-cache-interface
[assembly: CacheInterface(typeof(IFxRatesClient), nameof(IFxRatesClient.GetRateAsync),
    Duration = "00:01:00", Tags = ["fx", "fx:{from}"])]
// end-snippet

namespace Docs.ThirdParty
{
    public static class Setup
    {
        public static void Register(WebApplicationBuilder builder)
        {
            // begin-snippet: readme-cache-interface-registration
            builder.Services.AddSingleton<IFxRatesClient, FxRatesClient>();   // the vendor's client, registered as usual
            builder.Services.AddMethodCache();
            // end-snippet
        }

        public static async Task<decimal> ExplicitKey(ICacheManager cache, IFxRatesClient client, string from, string to)
        {
            // begin-snippet: readme-explicit-key
            var rate = await cache.GetOrCreateAsync(
                $"fx:{from}:{to}",
                async (ctx, ct) => await client.GetRateAsync(from, to),
                o => o.WithDuration(TimeSpan.FromMinutes(1)).WithTags("fx"));
            // end-snippet
            return rate;
        }
    }
}
