using MethodCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Docs.Expiration
{
    public sealed record Report(int Year);

    public sealed record Session(string Id);

    public sealed record PriceList(string Region);

    public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

    public sealed record SearchResult(IReadOnlyList<string> Items);

    public interface ITokenClient
    {
        [Cache(Duration = "1h")]
        Task<AccessToken> GetTokenAsync(string audience);
    }

    public interface ISearch
    {
        [Cache(Duration = "10m")]
        Task<SearchResult> FindAsync(string text);
    }

    // begin-snippet: expiration-attributes
    public interface IReportService
    {
        // Gone ten minutes after it was stored
        [Cache(Duration = "10m")]
        Task<Report> GetAsync(int year);

        // Kept while it is read at least every five minutes, but never longer than an hour
        [Cache(Duration = "1h", SlidingExpiration = "5m")]
        Task<Session> GetSessionAsync(string id);

        // Reloaded in the background when it is read in its last minute, so callers never wait
        [Cache(Duration = "10m", RefreshAhead = "1m")]
        Task<PriceList> GetPricesAsync(string region);

        // Changing the version changes every key, so values cached by the previous version are ignored
        [Cache(Duration = "1d", Version = 2)]
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

    public static class PerResult
    {
        public static void Register(WebApplicationBuilder builder)
        {
            // begin-snippet: expiration-duration-for
            builder.Services.AddMethodCache(mc =>
            {
                // A token is kept until a minute before it expires; one that expires sooner isn't cached
                mc.Method((ITokenClient c) => c.GetTokenAsync(Any<string>.Value))
                    .DurationFor(token => token.ExpiresAt - DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));

                // An empty result is kept for 30 seconds instead of ten minutes
                mc.Method((ISearch s) => s.FindAsync(Any<string>.Value))
                    .DurationFor(result => result.Items.Count == 0 ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(10));
            });
            // end-snippet
        }
    }
}
