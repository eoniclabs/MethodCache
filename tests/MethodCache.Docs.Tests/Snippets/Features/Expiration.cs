using MethodCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Docs.Expiration
{
    public sealed record Report(int Year);

    public sealed record Session(string Id);

    public sealed record PriceList(string Region);

    // begin-snippet: expiration-attributes
    public interface IReportService
    {
        // Gone ten minutes after it was stored
        [Cache(Duration = "00:10:00")]
        Task<Report> GetAsync(int year);

        // Kept while it is read at least every five minutes, but never longer than an hour
        [Cache(Duration = "01:00:00", SlidingExpiration = "00:05:00")]
        Task<Session> GetSessionAsync(string id);

        // Reloaded in the background when it is read in its last minute, so callers never wait
        [Cache(Duration = "00:10:00", RefreshAhead = "00:01:00")]
        Task<PriceList> GetPricesAsync(string region);

        // Changing the version changes every key, so values cached by the previous version are ignored
        [Cache(Duration = "1.00:00:00", Version = 2)]
        Task<Report> GetYearlyAsync(int year);
    }
    // end-snippet

    public static class Defaults
    {
        public static void Register(WebApplicationBuilder builder)
        {
            // begin-snippet: expiration-default
            builder.Services.AddMethodCache(config =>
                config.Defaults(o => o.WithDuration(TimeSpan.FromMinutes(15))));
            // end-snippet
        }
    }
}
