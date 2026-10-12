using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class NoCacheSafetyTests
{
    [Theory]
    [InlineData("refresh", false)]
    [InlineData("server-error", false)]
    [InlineData("exception", false)]
    [InlineData("server-error", true)]
    [InlineData("exception", true)]
    public async Task NoCache_RequiresSuccessfulValidation_BeforeReusingContent(string outcome, bool requestNoCache)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var options = new HttpCacheOptions
        {
            EnableStaleWhileRevalidate = outcome == "refresh",
            EnableStaleIfError = true,
            AddDiagnosticHeaders = true
        };
        var (client, origin) = HttpCacheTestFactory.CreateClient(options, timeProvider: time);
        using (client)
        {
            var initial = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("old"),
                Headers =
                {
                    Date = time.GetUtcNow(),
                    ETag = new EntityTagHeaderValue("\"v1\""),
                    CacheControl = new CacheControlHeaderValue
                    {
                        NoCache = !requestNoCache,
                        MaxAge = TimeSpan.FromSeconds(1),
                        Extensions =
                        {
                            new NameValueHeaderValue("stale-while-revalidate", "60"),
                            new NameValueHeaderValue("stale-if-error", "60")
                        }
                    }
                }
            };
            origin.SetResponses(initial, new HttpResponseMessage(outcome == "server-error" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent("new"),
                Headers = { CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) } }
            });
            using var first = await client.GetAsync("https://api.example.com/no-cache");
            time.Advance(TimeSpan.FromSeconds(2));
            origin.ShouldThrowException = outcome == "exception";
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/no-cache");
            if (requestNoCache)
            {
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            }

            if (outcome == "exception")
            {
                await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
            }
            else
            {
                using var response = await client.SendAsync(request);
                Assert.Equal(outcome == "server-error" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("new", await response.Content.ReadAsStringAsync());
                Assert.Equal(2, origin.RequestCount);
            }
        }
    }
}
