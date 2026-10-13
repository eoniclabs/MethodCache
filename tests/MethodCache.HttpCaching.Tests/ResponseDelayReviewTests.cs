using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Time.Testing;
using MethodCache.HttpCaching.Internal;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class ResponseDelayReviewTests
{
    [Fact]
    public void CorrectedAge_AdvancingResidentTime_DoesNotOverflow()
    {
        var time = new FakeTimeProvider();
        var entry = new HttpCacheEntry { RequestUri = "https://example.com/data", Method = "GET", StoredAt = time.GetUtcNow(), InitialAge = TimeSpan.MaxValue };
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.MaxValue, new FreshnessCalculator(new HttpCacheOptions(), time).GetCurrentAge(entry));
    }

    [Theory]
    [InlineData(20, 100, 5, 105)]
    [InlineData(100, 20, 5, 100)]
    [InlineData(0, 0, -5, 0)]
    public void RevalidatedAge_UsesCorrectedAgeAndApparentAge(int apparent, int age, int delay, int expected)
    {
        var time = new FakeTimeProvider();
        using var response = new HttpResponseMessage(HttpStatusCode.NotModified);
        response.Headers.Date = time.GetUtcNow() - TimeSpan.FromSeconds(apparent);
        response.Headers.Age = TimeSpan.FromSeconds(age);
        var entry = new HttpCacheEntry { RequestUri = "https://example.com/data", Method = "GET" }.WithUpdatedHeaders(response, time.GetUtcNow(), TimeSpan.FromSeconds(delay));
        Assert.Equal(TimeSpan.FromSeconds(expected), entry.InitialAge);
    }

    [Fact]
    public void RevalidatedAge_LargeAgeAndDelay_SaturateInsteadOfOverflowing()
    {
        var time = new FakeTimeProvider();
        using var response = new HttpResponseMessage(HttpStatusCode.NotModified);
        response.Headers.Age = TimeSpan.MaxValue - TimeSpan.FromSeconds(1);
        var entry = new HttpCacheEntry { RequestUri = "https://example.com/data", Method = "GET" }.WithUpdatedHeaders(response, time.GetUtcNow(), TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.MaxValue, entry.InitialAge);
    }

    [Fact]
    public async Task ResponseAge_IncludesTimeSpentWaitingForTheResponse()
    {
        var time = new FakeTimeProvider();
        var inner = new DelayedAgeHandler(time);
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions { EnableStaleIfError = false, EnableStaleWhileRevalidate = false }, inner, timeProvider: time));
        using var first = await client.GetAsync("https://example.com/data");
        using var second = await client.GetAsync("https://example.com/data");
        Assert.Equal(2, inner.Calls);
    }

    private sealed class DelayedAgeHandler(FakeTimeProvider time) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            time.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data"),
                Headers = { Date = time.GetUtcNow(), Age = TimeSpan.FromSeconds(100), CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(101) } }
            });
        }
    }
}
