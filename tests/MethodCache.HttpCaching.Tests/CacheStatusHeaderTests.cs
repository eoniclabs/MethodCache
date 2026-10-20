using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

/// <summary>
/// AddDiagnosticHeaders: the cache's RFC 9211 <c>Cache-Status</c> member says how each response was produced.
/// </summary>
public class CacheStatusHeaderTests
{
    private const string Url = "https://api.example.com/data";
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    private static string? Status(HttpResponseMessage response) => HttpCacheTestFactory.CacheStatus(response);

    private HttpResponseMessage Ok(string body = "v1", int maxAge = 60) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body),
        Headers =
        {
            CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(maxAge) },
            ETag = new EntityTagHeaderValue("\"" + body + "\""),
            Date = _time.GetUtcNow()
        }
    };

    private (HttpClient Client, TestHttpMessageHandler Origin) Client(HttpCacheOptions? options = null)
    {
        options ??= new HttpCacheOptions();
        options.AddDiagnosticHeaders = true;
        return HttpCacheTestFactory.CreateClient(options, timeProvider: _time);
    }

    [Fact]
    public async Task FirstRequest_IsAStoredUriMiss_AndTheNextAHitWithItsRemainingFreshness()
    {
        var (client, origin) = Client();
        origin.SetResponse(Ok());

        using var miss = await client.GetAsync(Url);
        _time.Advance(TimeSpan.FromSeconds(18));
        using var hit = await client.GetAsync(Url);

        Assert.Equal("MethodCache; fwd=uri-miss; fwd-status=200; stored", Status(miss));
        Assert.Equal("MethodCache; hit; ttl=42", Status(hit));
        Assert.Equal(TimeSpan.FromSeconds(18), hit.Headers.Age);
        Assert.DoesNotContain(miss.Headers.Concat(hit.Headers), header => header.Key.StartsWith("X-Cache", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResponseTheCacheMayNotStore_IsAMissWithoutStored()
    {
        var (client, origin) = Client();
        var response = Ok();
        response.Headers.CacheControl!.NoStore = true;
        origin.SetResponse(response);

        using var miss = await client.GetAsync(Url);

        Assert.Equal("MethodCache; fwd=uri-miss; fwd-status=200", Status(miss));
    }

    [Fact]
    public async Task ResponseForOtherVaryValues_IsAVaryMiss()
    {
        var (client, origin) = Client();
        var english = Ok("en");
        english.Headers.Vary.Add("Accept-Language");
        var french = Ok("fr");
        french.Headers.Vary.Add("Accept-Language");
        origin.SetResponses(english, french);

        using var first = await client.SendAsync(Get(language: "en"));
        using var second = await client.SendAsync(Get(language: "fr"));

        Assert.Equal("MethodCache; fwd=uri-miss; fwd-status=200; stored", Status(first));
        Assert.Equal("MethodCache; fwd=vary-miss; fwd-status=200; stored", Status(second));
    }

    [Fact]
    public async Task StaleResponseRevalidatedWith304_IsAStaleForward_ThatStoredTheUpdatedResponse()
    {
        var (client, origin) = Client();
        origin.SetResponse(Ok());
        using var _ = await client.GetAsync(Url);
        _time.Advance(TimeSpan.FromSeconds(61));
        origin.SetResponse(new HttpResponseMessage(HttpStatusCode.NotModified)
        {
            Headers =
            {
                CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(60) },
                ETag = new EntityTagHeaderValue("\"v1\""),
                Date = _time.GetUtcNow()
            }
        });

        using var revalidated = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.OK, revalidated.StatusCode);
        Assert.Equal("MethodCache; fwd=stale; fwd-status=304; stored; ttl=60", Status(revalidated));
    }

    [Fact]
    public async Task RequestNoCache_OnAFreshResponse_IsARequestForward()
    {
        var (client, origin) = Client();
        origin.SetResponses(Ok("v1"), Ok("v2"));
        using var _ = await client.GetAsync(Url);

        using var request = Get();
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await client.SendAsync(request);

        Assert.Equal("MethodCache; fwd=request; fwd-status=200; stored", Status(response));
    }

    [Fact]
    public async Task OriginServerError_ServedStale_ReportsTheOriginsStatus()
    {
        var (client, origin) = Client(new HttpCacheOptions { EnableStaleIfError = true, MaxStaleIfError = TimeSpan.FromSeconds(30) });
        origin.SetResponse(Ok());
        using var _ = await client.GetAsync(Url);
        _time.Advance(TimeSpan.FromSeconds(70));
        origin.SetResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        using var stale = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Assert.Equal("MethodCache; fwd=stale; fwd-status=503; ttl=-10; detail=stale-if-error", Status(stale));
    }

    [Fact]
    public async Task UnsafeMethod_IsAMethodForward()
    {
        var (client, origin) = Client();
        origin.SetResponse(new HttpResponseMessage(HttpStatusCode.Created));

        using var response = await client.PostAsync(Url, new StringContent("{}"));

        Assert.Equal("MethodCache; fwd=method; fwd-status=201", Status(response));
    }

    [Fact]
    public async Task RequestTheCacheDoesNotHandle_IsABypass()
    {
        var (client, origin) = Client();
        origin.SetResponse(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StringContent("v") });

        using var request = Get();
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await client.SendAsync(request);

        Assert.Equal("MethodCache; fwd=bypass; fwd-status=206", Status(response));
    }

    [Fact]
    public async Task UnavailableStorage_IsABypass_ThatSaysWhy()
    {
        var storage = Substitute.For<IHttpCacheStorage>();
        storage.ForClient(Arg.Any<string>(), Arg.Any<HttpCacheOptions>()).Returns(storage);
        storage.GetGenerationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<string>("unavailable:1"));
        var origin = new TestHttpMessageHandler();
        origin.SetResponse(Ok());
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions { AddDiagnosticHeaders = true }, origin, storage, _time));

        using var response = await client.GetAsync(Url);

        Assert.Equal("MethodCache; fwd=bypass; fwd-status=200; detail=storage-unavailable", Status(response));
    }

    [Fact]
    public async Task FollowedRedirect_ReportsTheFinalStatus_WithoutStored()
    {
        var origin = new RedirectingOrigin(Ok());
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions { AddDiagnosticHeaders = true }, origin, timeProvider: _time));

        using var response = await client.GetAsync(Url);

        Assert.Equal("MethodCache; fwd=uri-miss; fwd-status=200", Status(response));
    }

    [Fact]
    public async Task OnlyIfCached_WithNothingStored_AddsNoMember_BecauseTheCacheMadeTheResponse()
    {
        var (client, _) = Client();

        using var request = Get();
        request.Headers.CacheControl = new CacheControlHeaderValue { OnlyIfCached = true };
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.False(response.Headers.Contains("Cache-Status"));
    }

    [Fact]
    public async Task MembersFromTheOriginsCaches_AreKept_AndThisCachesMemberComesLast()
    {
        const string upstream = "OriginCache; hit; ttl=1100, \"CDN Company Here\"; fwd=uri-miss; stored";
        var (client, origin) = Client();
        var response = Ok();
        response.Headers.TryAddWithoutValidation("Cache-Status", upstream);
        origin.SetResponse(response);

        using var miss = await client.GetAsync(Url);
        using var hit = await client.GetAsync(Url);

        Assert.Equal(new[] { upstream, "MethodCache; fwd=uri-miss; fwd-status=200; stored" }, miss.Headers.GetValues("Cache-Status"));
        // The stored response never carries this cache's own member: a hit has exactly one, the last
        Assert.Equal(new[] { upstream, "MethodCache; hit; ttl=60" }, hit.Headers.GetValues("Cache-Status"));
    }

    [Fact]
    public async Task WithoutDiagnosticHeaders_NoMemberIsAdded_ButAgeIsStillSet()
    {
        var (client, origin) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions(), timeProvider: _time);
        origin.SetResponse(Ok());

        using var miss = await client.GetAsync(Url);
        _time.Advance(TimeSpan.FromSeconds(5));
        using var hit = await client.GetAsync(Url);

        Assert.False(miss.Headers.Contains("Cache-Status"));
        Assert.False(hit.Headers.Contains("Cache-Status"));
        Assert.Equal(TimeSpan.FromSeconds(5), hit.Headers.Age);
    }

    private static HttpRequestMessage Get(string? language = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Url);
        if (language != null) request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(language));
        return request;
    }

    /// <summary>Follows a redirect the way a transport does: it changes the request message it was given.</summary>
    private sealed class RedirectingOrigin(HttpResponseMessage target) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new Uri("https://api.example.com/moved");
            var response = TestHttpMessageHandler.CloneResponse(target);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
