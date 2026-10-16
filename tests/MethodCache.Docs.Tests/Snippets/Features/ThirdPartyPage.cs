using FxVendor;
using MethodCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Docs.ThirdPartyPage
{
    public static class Setup
    {
        public static void PerMethod(WebApplicationBuilder builder)
        {
            // begin-snippet: third-party-per-method
            builder.Services.AddMethodCache(config => config.Method((IFxRatesClient c) => c.GetRateAsync(Any<string>.Value, Any<string>.Value))
                .Configure(o => o.WithDuration(TimeSpan.FromSeconds(30))));
            // end-snippet
        }

        public static void Wrapper(WebApplicationBuilder builder)
        {
            // begin-snippet: third-party-wrapper-registration
            builder.Services.AddSingleton<FxRatesClient>();
            builder.Services.AddScoped<IRates, Rates>();
            builder.Services.AddMethodCache();
            // end-snippet
        }
    }

    // begin-snippet: third-party-wrapper
    public interface IRates
    {
        [Cache(Duration = "00:01:00", Tags = ["fx", "fx:{from}"])]
        Task<decimal> GetAsync(string from, string to);
    }

    public sealed class Rates(FxRatesClient client) : IRates   // calls the vendor's concrete class
    {
        public Task<decimal> GetAsync(string from, string to) => client.GetRateAsync(from, to);
    }
    // end-snippet
}
