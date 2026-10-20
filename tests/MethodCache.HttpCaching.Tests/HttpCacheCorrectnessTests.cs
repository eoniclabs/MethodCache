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

    [Theory]
    [InlineData("OPTIONS")]
    [InlineData("TRACE")]
    public async Task OtherSafeMethods_AreNotCached_AndDoNotInvalidate(string method)
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        inner.SetResponses(Cacheable("v1"), Cacheable("a"), Cacheable("b"));

        Assert.Equal("v1", await client.GetStringAsync(Url));
        await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), Url));
        await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), Url));

        Assert.Equal("v1", await client.GetStringAsync(Url));
        Assert.Equal(3, inner.RequestCount);
    }

    [Theory]
    [InlineData("QUERY")]
    [InlineData("PROPFIND")]
    public async Task MethodOfUnknownSafety_IsNotCached_AndInvalidates(string method)
    {
        // RFC 9111 section 4.4: methods whose safety is unknown invalidate like unsafe ones
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        inner.SetResponses(Cacheable("v1"), Cacheable("a"), Cacheable("b"), Cacheable("v2"));

        Assert.Equal("v1", await client.GetStringAsync(Url));
        await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), Url));
        await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), Url));

        Assert.Equal("v2", await client.GetStringAsync(Url));
        Assert.Equal(4, inner.RequestCount);
    }

    [Fact]
    public async Task HeadResponses_AreCached()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        inner.SetResponses(Cacheable("a"), Cacheable("b"));

        await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Url));
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Url));

        Assert.Equal(1, inner.RequestCount);
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
    public async Task VaryVariants_AreCachedSideBySide()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        inner.SetResponses(Variant("sv", "sv"), Variant("en", "en"), Variant("de", "de"));

        await GetInLanguage(client, "sv");
        await GetInLanguage(client, "en");
        await GetInLanguage(client, "de");

        Assert.Equal("sv", await GetInLanguage(client, "sv"));
        Assert.Equal("en", await GetInLanguage(client, "en"));
        Assert.Equal(3, inner.RequestCount);
    }

    [Fact]
    public async Task UnsafeRequest_InvalidatesEveryVariant_AndOldVariantsAreNeverServedAgain()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        inner.SetResponses(Variant("sv-1", "sv"), Variant("en-1", "en"), new HttpResponseMessage(HttpStatusCode.NoContent),
            Variant("en-2", "en"), Variant("sv-2", "sv"));

        await GetInLanguage(client, "sv");
        await GetInLanguage(client, "en");
        await client.PostAsync(Url, new StringContent("x"));

        Assert.Equal("en-2", await GetInLanguage(client, "en"));
        Assert.Equal("sv-2", await GetInLanguage(client, "sv"));
        Assert.Equal(5, inner.RequestCount);
    }

    [Fact]
    public async Task UnsafeRequest_InvalidatesEveryKeyHeaderValue()
    {
        var options = new HttpCacheOptions();
        options.KeyHeaders.Add("X-Tenant");
        var (client, inner) = HttpCacheTestFactory.CreateClient(options);
        inner.SetResponses(Cacheable("a-1"), Cacheable("b-1"), new HttpResponseMessage(HttpStatusCode.NoContent), Cacheable("b-2"));

        await GetForTenant(client, "a");
        await GetForTenant(client, "b");
        using (var post = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StringContent("x") })
        {
            post.Headers.Add("X-Tenant", "a");
            await client.SendAsync(post);
        }

        Assert.Equal("b-2", await GetForTenant(client, "b"));
    }

    [Fact]
    public async Task PrivateCache_UnsafeRequestByOneCaller_InvalidatesOtherCallers()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        inner.SetResponses(Cacheable("alice-1"), Cacheable("bob-1"), new HttpResponseMessage(HttpStatusCode.NoContent), Cacheable("bob-2"));

        Assert.Equal("alice-1", await GetAs(client, "alice"));
        Assert.Equal("bob-1", await GetAs(client, "bob"));
        using (var post = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StringContent("x") })
        {
            post.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "alice");
            await client.SendAsync(post);
        }

        Assert.Equal("bob-2", await GetAs(client, "bob"));
    }

    [Fact]
    public async Task PrivateCache_CallersNeverSeeEachOthersResponses()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        inner.SetResponses(Cacheable("alice"), Cacheable("bob"));

        Assert.Equal("alice", await GetAs(client, "alice"));
        Assert.Equal("bob", await GetAs(client, "bob"));
        Assert.Equal("alice", await GetAs(client, "alice"));
        Assert.Equal(2, inner.RequestCount);
    }

    [Fact]
    public async Task ClearAsync_OnlyClearsTheHttpCache()
    {
        using var appCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        Microsoft.Extensions.Caching.Memory.CacheExtensions.Set(appCache, "app", 42);
        using var storage = new TestHttpStore();

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
    public async Task CacheablePostResponses_AreNeverCached_AndStillInvalidate()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
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

    [Theory]
    [InlineData("range")]
    [InlineData("no-store")]
    [InlineData("conditional")]
    [InlineData("method")]
    public async Task OnlyIfCached_BypassRequest_DoesNotContactOrigin(string reason)
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        using var owner = client;
        inner.SetResponse(Cacheable("origin"));
        using var request = new HttpRequestMessage(reason == "method" ? HttpMethod.Post : HttpMethod.Get, Url);
        request.Headers.CacheControl = new CacheControlHeaderValue { OnlyIfCached = true, NoStore = reason == "no-store" };
        if (reason == "range") request.Headers.Range = new RangeHeaderValue(0, 1);
        if (reason == "conditional") request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"caller\""));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(0, inner.RequestCount);
    }

    [Fact]
    public async Task OnlyIfCached_StaleWhileRevalidate_DoesNotStartBackgroundOriginRequest()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions { EnableStaleWhileRevalidate = true }, timeProvider: clock);
        using var owner = client;
        var first = Cacheable("cached", maxAge: TimeSpan.FromSeconds(1));
        first.Headers.CacheControl!.Extensions.Add(new NameValueHeaderValue("stale-while-revalidate", "60"));
        inner.SetResponses(first, Cacheable("origin"));
        using var initial = await client.GetAsync(Url);
        clock.Advance(TimeSpan.FromSeconds(2));
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.CacheControl = new CacheControlHeaderValue { OnlyIfCached = true };

        using var response = await client.SendAsync(request);

        Assert.Equal("cached", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, inner.RequestCount);
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
    public async Task ResponsesLargerThanMaxResponseBytes_AreNotCached()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions { MaxResponseBytes = 4 });
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

    private static async Task<string> GetForTenant(HttpClient client, string tenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Add("X-Tenant", tenant);
        using var response = await client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<string> GetAs(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
        private readonly Dictionary<string, string> _generations = new();
        public ValueTask<string> GetGenerationAsync(string key, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_generations.GetValueOrDefault(key, string.Empty));
        public ValueTask InvalidateAsync(string key, CancellationToken cancellationToken = default)
        {
            _generations[key] = Guid.NewGuid().ToString("N");
            return ValueTask.CompletedTask;
        }

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
