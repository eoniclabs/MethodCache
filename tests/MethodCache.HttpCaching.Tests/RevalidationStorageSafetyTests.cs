using System.Net;
using System.Net.Http.Headers;
using MethodCache.HttpCaching.Internal;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class RevalidationStorageSafetyTests
{
    private const string Url = "https://api.example.com/revalidation-policy";

    [Theory]
    [InlineData("no-store", true, false)]
    [InlineData("no-store", false, false)]
    [InlineData("private", true, false)]
    [InlineData("vary-star", true, false)]
    [InlineData("vary-star", false, false)]
    [InlineData("no-store", true, true)]
    [InlineData("no-store", false, true)]
    [InlineData("private", true, true)]
    [InlineData("vary-star", true, true)]
    [InlineData("vary-star", false, true)]
    public async Task Revalidation_ForbidsStorage_RemovesPreviousEntry(string directive, bool shared, bool background)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var storage = HttpCacheTestFactory.CreateInMemoryStorage(timeProvider: time);
        var options = new HttpCacheOptions { IsSharedCache = shared, EnableStaleWhileRevalidate = background };
        using var origin = new PolicyOrigin(directive, background);
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(options, origin, storage, time));
        using var first = await client.GetAsync(Url);
        time.Advance(TimeSpan.FromSeconds(2));

        using var validated = await client.GetAsync(Url);
        await origin.RevalidationFinished.Task;

        Assert.Null(await storage.GetAsync(HttpCacheKeys.ForUri("test", HttpMethod.Get, new Uri(Url))));
        using var third = await client.GetAsync(Url);
        Assert.Equal("new", await third.Content.ReadAsStringAsync());
        Assert.Equal(3, origin.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revalidation_PrivateResponse_InPrivateCache_RemainsReusable(bool background)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var origin = new PolicyOrigin("private", background);
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(
            new HttpCacheOptions { EnableStaleWhileRevalidate = background }, origin, timeProvider: time));
        using var first = await client.GetAsync(Url);
        time.Advance(TimeSpan.FromSeconds(2));
        using var validated = await client.GetAsync(Url);
        await origin.RevalidationFinished.Task;
        using var cached = await client.GetAsync(Url);
        Assert.Equal("old", await cached.Content.ReadAsStringAsync());
        Assert.Equal(2, origin.Calls);
    }

    [Fact]
    public async Task Revalidation_OmitsVary_PreservesLanguageIsolation()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var origin = new LanguageOrigin();
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions(), origin, timeProvider: time));
        using var firstRequest = new HttpRequestMessage(HttpMethod.Get, Url);
        firstRequest.Headers.AcceptLanguage.ParseAdd("en");
        using var first = await client.SendAsync(firstRequest);
        time.Advance(TimeSpan.FromSeconds(2));
        using var validationRequest = new HttpRequestMessage(HttpMethod.Get, Url);
        validationRequest.Headers.AcceptLanguage.ParseAdd("en");
        using var validated = await client.SendAsync(validationRequest);
        using var frenchRequest = new HttpRequestMessage(HttpMethod.Get, Url);
        frenchRequest.Headers.AcceptLanguage.ParseAdd("fr");
        using var french = await client.SendAsync(frenchRequest);
        Assert.Equal("fr", await french.Content.ReadAsStringAsync());
        Assert.Equal(3, origin.Calls);
    }

    private sealed class LanguageOrigin : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(Calls == 2 ? HttpStatusCode.NotModified : HttpStatusCode.OK)
            {
                Headers = { CacheControl = CacheControlHeaderValue.Parse(Calls == 1 ? "max-age=1" : "max-age=300") }
            };
            if (Calls != 2)
            {
                response.Content = new StringContent(request.Headers.AcceptLanguage.Single().Value);
                response.Headers.Vary.Add("Accept-Language");
                response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            }
            return Task.FromResult(response);
        }
    }

    private sealed class PolicyOrigin(string directive, bool background) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource RevalidationFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 2)
            {
                Assert.Contains(request.Headers.IfNoneMatch, tag => tag.Tag == "\"v1\"");
                var validated = new HttpResponseMessage(HttpStatusCode.NotModified)
                {
                    Content = new CompletionContent(RevalidationFinished),
                    Headers = { CacheControl = CacheControlHeaderValue.Parse(directive == "vary-star" ? "max-age=300" : $"{directive}, max-age=300") }
                };
                if (directive == "vary-star")
                {
                    validated.Headers.Vary.Add("*");
                }
                return Task.FromResult(validated);
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(call == 1 ? "old" : "new"),
                Headers =
                {
                    ETag = new EntityTagHeaderValue("\"v1\""),
                    CacheControl = CacheControlHeaderValue.Parse(background && call == 1 ? "max-age=1, stale-while-revalidate=60" : "max-age=1")
                }
            };
            return Task.FromResult(response);
        }
    }

    private sealed class CompletionContent(TaskCompletionSource completion) : ByteArrayContent([])
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                completion.TrySetResult();
            }
        }
    }
}
