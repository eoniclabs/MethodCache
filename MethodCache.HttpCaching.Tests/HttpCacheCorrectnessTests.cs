using MethodCache.HttpCaching.Internal;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class HttpCacheCorrectnessTests
{
    private const string Url = "https://api.example.com/items/1";

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task SuccessfulUnsafeRequest_InvalidatesCachedGet(string method)
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        inner.SetResponses(Cacheable("v1"), new HttpResponseMessage(HttpStatusCode.NoContent), Cacheable("v2"));

        Assert.Equal("v1", await client.GetStringAsync(Url));
        await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), Url));
        Assert.Equal("v2", await client.GetStringAsync(Url));

        Assert.Equal(3, inner.RequestCount);
    }

    [Fact]
    public async Task FailedUnsafeRequest_KeepsCachedGet()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        inner.SetResponses(Cacheable("v1"), new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await client.GetStringAsync(Url);
        await client.PostAsync(Url, new StringContent("x"));

        Assert.Equal("v1", await client.GetStringAsync(Url));
        Assert.Equal(2, inner.RequestCount);
    }

    [Fact]
    public async Task UnsafeRequest_InvalidatesSameOriginLocation_ButNotOtherHosts()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        var created = new HttpResponseMessage(HttpStatusCode.Created);
        created.Headers.Location = new Uri("/items/1", UriKind.Relative);
        var elsewhere = new HttpResponseMessage(HttpStatusCode.Created);
        elsewhere.Headers.Location = new Uri("https://other.example.com/items/1");
        inner.SetResponses(Cacheable("v1"), Cacheable("o1"), created, elsewhere, Cacheable("v2"));

        await client.GetStringAsync(Url);
        await client.GetStringAsync("https://other.example.com/items/1");
        await client.PostAsync("https://api.example.com/items", new StringContent("x"));
        await client.PostAsync("https://api.example.com/items", new StringContent("x"));

        Assert.Equal("v2", await client.GetStringAsync(Url));
        Assert.Equal("o1", await client.GetStringAsync("https://other.example.com/items/1"));
    }

    [Fact]
    public async Task StaleEntryWithETag_IsKeptAndRevalidated()
    {
        var clock = new ManualTimeProvider();
        var storage = new RecordingStorage();
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions(), storage: storage, timeProvider: clock);

        var first = Cacheable("v1", maxAge: TimeSpan.FromSeconds(60));
        first.Headers.ETag = new EntityTagHeaderValue("\"e1\"");
        var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
        notModified.Headers.ETag = new EntityTagHeaderValue("\"e1\"");
        inner.SetResponses(first, notModified);

        await client.GetStringAsync(Url);
        clock.Advance(TimeSpan.FromMinutes(2));
        var response = await client.GetAsync(Url);

        // Kept for its 60 s of freshness plus the stale retention, so it can still be revalidated
        Assert.Equal(TimeSpan.FromSeconds(60) + TimeSpan.FromHours(24), storage.TimeToLives[0]);
        Assert.Equal("\"e1\"", inner.Requests[1].Headers.IfNoneMatch.Single().Tag);
        Assert.Equal("v1", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ResponseWithOnlyETag_IsRevalidatedEveryTime()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        var first = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("v1") };
        first.Headers.ETag = new EntityTagHeaderValue("\"e1\"");
        inner.SetResponses(first, new HttpResponseMessage(HttpStatusCode.NotModified), new HttpResponseMessage(HttpStatusCode.NotModified));

        await client.GetStringAsync(Url);
        Assert.Equal("v1", await client.GetStringAsync(Url));
        Assert.Equal("v1", await client.GetStringAsync(Url));

        Assert.Equal(3, inner.RequestCount);
        Assert.All(inner.Requests.Skip(1), r => Assert.Equal("\"e1\"", r.Headers.IfNoneMatch.Single().Tag));
    }

    [Fact]
    public async Task AgeHeaderFromOrigin_CountsTowardsFreshness()
    {
        var clock = new ManualTimeProvider();
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions(), timeProvider: clock);
        var aged = Cacheable("v1", maxAge: TimeSpan.FromSeconds(300));
        aged.Headers.Age = TimeSpan.FromSeconds(250);
        inner.SetResponses(aged, Cacheable("v2"));

        await client.GetStringAsync(Url);
        clock.Advance(TimeSpan.FromSeconds(30));
        var hit = await client.GetAsync(Url);
        clock.Advance(TimeSpan.FromSeconds(30));
        var refetched = await client.GetStringAsync(Url);

        Assert.Equal(TimeSpan.FromSeconds(280), hit.Headers.Age);
        Assert.Equal("v2", refetched);
    }

    [Fact]
    public async Task CachedResponse_HasRequestMessage()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        inner.SetResponses(Cacheable("v1"));

        await client.GetStringAsync(Url);
        var hit = await client.GetAsync(Url);

        Assert.NotNull(hit.RequestMessage);
        Assert.Equal(new Uri(Url), hit.RequestMessage!.RequestUri);
    }

    [Fact]
    public async Task OptionsRequests_AreNotCached()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        inner.SetResponses(Cacheable("a"), Cacheable("b"));

        await client.SendAsync(new HttpRequestMessage(HttpMethod.Options, Url));
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Options, Url));

        Assert.Equal(2, inner.RequestCount);
    }

    [Fact]
    public async Task MaxVariantsPerUrl_EvictsTheOldestVariant()
    {
        var options = new HttpCacheOptions();
        options.MaxVariantsPerUrl = 2;
        var (client, inner) = HttpCacheTestFactory.CreateClient(options);
        inner.SetResponses(Variant("sv", "sv"), Variant("en", "en"), Variant("de", "de"), Variant("sv-again", "sv"));

        await GetInLanguage(client, "sv");
        await GetInLanguage(client, "en");
        await GetInLanguage(client, "de");

        Assert.Equal("en", await GetInLanguage(client, "en"));
        Assert.Equal("sv-again", await GetInLanguage(client, "sv"));
        Assert.Equal(4, inner.RequestCount);
    }

    [Fact]
    public async Task ClearAsync_OnlyClearsTheHttpCache()
    {
        using var appCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        Microsoft.Extensions.Caching.Memory.CacheExtensions.Set(appCache, "app", 42);
        using var storage = new HttpCacheStore(
            Microsoft.Extensions.Options.Options.Create(new HttpCacheStorageOptions()),
            NullLogger<HttpCacheStore>.Instance);

        await storage.ClearAsync();

        Assert.True(appCache.TryGetValue("app", out _));
    }

    [Fact]
    public async Task StaleIfError_ServesStaleContent_WhenOriginReturns503()
    {
        var clock = new ManualTimeProvider();
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions { EnableStaleIfError = true }, timeProvider: clock);
        inner.SetResponses(Cacheable("v1", maxAge: TimeSpan.FromSeconds(60)), new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await client.GetStringAsync(Url);
        clock.Advance(TimeSpan.FromMinutes(5));
        var response = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("v1", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task StaleIfError_ServesStaleContent_WhenHttpClientTimesOut()
    {
        var clock = new ManualTimeProvider();
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions { EnableStaleIfError = true }, timeProvider: clock);
        inner.SetResponses(Cacheable("v1", maxAge: TimeSpan.FromSeconds(60)));

        await client.GetStringAsync(Url);
        clock.Advance(TimeSpan.FromMinutes(5));
        inner.ExceptionToThrow = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");

        Assert.Equal("v1", await client.GetStringAsync(Url));
    }

    [Fact]
    public async Task StaleIfError_DoesNotSwallowCallerCancellation()
    {
        var clock = new ManualTimeProvider();
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions { EnableStaleIfError = true }, timeProvider: clock);
        inner.SetResponses(Cacheable("v1", maxAge: TimeSpan.FromSeconds(60)));

        await client.GetStringAsync(Url);
        clock.Advance(TimeSpan.FromMinutes(5));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        inner.ExceptionToThrow = new OperationCanceledException(cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetStringAsync(Url, cancellation.Token));
    }

    [Fact]
    public async Task UnsafeMethodInCacheableMethods_IsNotCached_AndStillInvalidates()
    {
        var options = new HttpCacheOptions();
        options.CacheableMethods.Add(HttpMethod.Post);
        var (client, inner) = HttpCacheTestFactory.CreateClient(options);
        inner.SetResponses(Cacheable("v1"), Cacheable("posted"), Cacheable("posted again"), Cacheable("v2"));

        Assert.Equal("v1", await client.GetStringAsync(Url));
        await client.PostAsync(Url, new StringContent("x"));
        await client.PostAsync(Url, new StringContent("x"));
        Assert.Equal("v2", await client.GetStringAsync(Url));

        Assert.Equal(4, inner.RequestCount);
    }

    [Fact]
    public async Task MustRevalidate_IsNeverServedStale()
    {
        var clock = new ManualTimeProvider();
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions { EnableStaleIfError = true }, timeProvider: clock);
        var first = Cacheable("v1", maxAge: TimeSpan.FromSeconds(60));
        first.Headers.CacheControl!.MustRevalidate = true;
        inner.SetResponses(first, new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await client.GetStringAsync(Url);
        clock.Advance(TimeSpan.FromMinutes(5));
        var response = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task OnlyIfCached_ReturnsGatewayTimeout_InsteadOfCallingTheOrigin()
    {
        var clock = new ManualTimeProvider();
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions(), timeProvider: clock);
        inner.SetResponses(Cacheable("v1", maxAge: TimeSpan.FromSeconds(60)));

        await client.GetStringAsync(Url);
        clock.Advance(TimeSpan.FromMinutes(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.CacheControl = new CacheControlHeaderValue { OnlyIfCached = true };
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(1, inner.RequestCount);
    }

    [Fact]
    public async Task SharedCache_DoesNotStoreAuthorizedResponses_UnlessPublic()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions { IsSharedCache = true });
        var publicResponse = Cacheable("public");
        publicResponse.Headers.CacheControl!.Public = true;
        inner.SetResponses(Cacheable("a"), Cacheable("b"), publicResponse);

        await GetAuthorized(client, Url);
        await GetAuthorized(client, Url);
        await GetAuthorized(client, Url + "?public");

        Assert.Equal("public", await GetAuthorized(client, Url + "?public"));
        Assert.Equal(3, inner.RequestCount);
    }

    [Fact]
    public async Task ResponsesLargerThanMaxResponseSize_AreNotCached()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions { MaxResponseSize = 4 });
        inner.SetResponses(Cacheable("too large"), Cacheable("too large"));

        await client.GetStringAsync(Url);
        await client.GetStringAsync(Url);

        Assert.Equal(2, inner.RequestCount);
    }

    private static async Task<string> GetAuthorized(HttpClient client, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token");
        using var response = await client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<string> GetInLanguage(HttpClient client, string language)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.AcceptLanguage.ParseAdd(language);
        using var response = await client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    private static HttpResponseMessage Variant(string body, string language)
    {
        var response = Cacheable(body);
        response.Headers.Vary.Add("Accept-Language");
        response.Content.Headers.ContentLanguage.Add(language);
        return response;
    }

    private static HttpResponseMessage Cacheable(string body, TimeSpan? maxAge = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = maxAge ?? TimeSpan.FromMinutes(10) };
        return response;
    }

    private sealed class RecordingStorage : IHttpCacheStorage
    {
        private readonly Dictionary<string, HttpCacheEntry> _entries = new();

        public List<TimeSpan> TimeToLives { get; } = new();

        public ValueTask<HttpCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_entries.GetValueOrDefault(key));

        public ValueTask SetAsync(string key, HttpCacheEntry entry, TimeSpan timeToLive, CancellationToken cancellationToken = default)
        {
            _entries[key] = entry;
            TimeToLives.Add(timeToLive);
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _entries.Remove(key);
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync(CancellationToken cancellationToken = default)
        {
            _entries.Clear();
            return ValueTask.CompletedTask;
        }
    }
}
