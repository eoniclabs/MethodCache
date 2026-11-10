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

    // The price in effect now, and when the next future-dated price takes over (null when none is scheduled)
    public sealed record CurrentPrice(decimal Amount, DateTimeOffset? NextChangeAt);

    public interface IPriceSchedule
    {
        [Cache(Duration = "1h")]
        Task<CurrentPrice> GetCurrentAsync(string product);
    }

    public interface ISearch
    {
        [Cache(Duration = "10m")]
        Task<SearchResult> FindAsync(string text);
    }

    // A page's title and image for a link, or Succeeded false when the page couldn't be read
    public sealed record LinkPreview(bool Succeeded, string? Title);

    public interface ILinkPreviews
    {
        [Cache]
        Task<LinkPreview> GetAsync(string url);
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

    public sealed record Layout(string Name);

    // begin-snippet: expiration-sliding-only
    public interface ILayouts
    {
        // Kept while it is read at least every 30 minutes, up to MaxExpiration (7 days)
        [Cache(SlidingExpiration = "30m")]
        Task<Layout> GetAsync(string name);
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

                // A price is kept until the next one takes effect, so a future-dated price applies on time
                mc.Method((IPriceSchedule s) => s.GetCurrentAsync(Any<string>.Value))
                    .DurationFor(price => price.NextChangeAt is { } next ? next - DateTimeOffset.UtcNow : TimeSpan.FromHours(1));
            });
            // end-snippet
        }

        public static void RegisterSliding(WebApplicationBuilder builder)
        {
            // begin-snippet: expiration-sliding-for
            builder.Services.AddMethodCache(mc =>
                // A preview that loaded is kept while it is read at least every 30 minutes, for at most a day.
                // One that failed is kept for two hours, read or not, and then the page is tried again.
                mc.Method((ILinkPreviews p) => p.GetAsync(Any<string>.Value))
                    .DurationFor(preview => preview.Succeeded ? TimeSpan.FromDays(1) : TimeSpan.FromHours(2))
                    .SlidingFor(preview => preview.Succeeded ? TimeSpan.FromMinutes(30) : TimeSpan.Zero));
            // end-snippet
        }
    }
}
