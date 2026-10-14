using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class RevalidationReviewTests
{
    private const string Url = "https://example.com/item";

    [Theory]
    [InlineData("max-age=0, only-if-cached")]
    [InlineData("min-fresh=120, only-if-cached")]
    [InlineData("max-stale=0, only-if-cached")]
    public async Task StaleWhileRevalidate_RequestFreshnessConstraint_IsHonored(string directive)
    {
        var clock = new ManualTimeProvider();
        var (client, origin) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions { EnableStaleWhileRevalidate = true }, timeProvider: clock);
        using var owned = client;
        origin.SetResponse(Response("old", "max-age=60, stale-while-revalidate=120"));
        await client.GetStringAsync(Url);
        clock.Advance(TimeSpan.FromSeconds(61));
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.CacheControl = CacheControlHeaderValue.Parse(directive);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Theory]
    [InlineData("no-store")]
    [InlineData("private, max-age=600")]
    [InlineData("max-age=0")]
    public async Task FullRevalidation_UnstorableReplacement_DoesNotLeaveOlderFreshEntry(string directive)
    {
        var (client, origin) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions { IsSharedCache = true });
        using var owned = client;
        origin.SetResponses(Response("old", "max-age=600"), Response("new", directive), Response("latest", "max-age=600"));
        Assert.Equal("old", await client.GetStringAsync(Url));
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var replacement = await client.SendAsync(request);
        Assert.Equal("new", await replacement.Content.ReadAsStringAsync());

        Assert.Equal("latest", await client.GetStringAsync(Url));
    }

    private static HttpResponseMessage Response(string body, string directives)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        response.Headers.CacheControl = CacheControlHeaderValue.Parse(directives);
        return response;
    }
}
