using MethodCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Docs.FailSafe
{
    public sealed record Rates(decimal EurSek);

    public static class Setup
    {
        public static void Register(WebApplicationBuilder builder)
        {
            // begin-snippet: failsafe-central
            builder.Services.AddMethodCache(config => config.Defaults(o => o
                .WithFailSafe(TimeSpan.FromHours(1))                    // how long an expired value is kept as a fallback
                .WithFailSafeTimeout(TimeSpan.FromMilliseconds(200))    // optional: don't wait longer than this when a fallback exists
                .WithFailSafeRetryAfter(TimeSpan.FromSeconds(30))));    // optional: reuse the fallback this long before trying again
            // end-snippet
        }
    }

    // begin-snippet: failsafe-per-method
    public interface IAccountService
    {
        [Cache(Duration = "5m", FailSafe = "1h", FailSafeTimeout = "0.2s")]
        Task<Rates> GetRatesAsync();

        [Cache(FailSafe = "off")]   // a balance must never be old, so an error is better
        Task<decimal> GetBalanceAsync(int accountId);
    }
    // end-snippet
}
