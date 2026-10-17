using System.Net;
using System.Net.Http.Headers;
using MethodCache.HttpCaching.Internal;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

/// <summary>
/// Pins max-age, stale-while-revalidate and stale-if-error to their exact boundaries with a fake clock: one tick
/// before the boundary the cached response is still usable that way, at the boundary it is not.
/// </summary>
public class FreshnessBoundaryTests
{
    private const string Url = "https://api.example.com/boundary";
    private static readonly TimeSpan OneTick = TimeSpan.FromTicks(1);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    private HttpResponseMessage Response(string content, TimeSpan maxAge, string? extension = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content),
            Headers = { CacheControl = new CacheControlHeaderValue { MaxAge = maxAge }, Date = _time.GetUtcNow() }
        };

        if (extension != null)
        {
            var parts = extension.Split('=');
            response.Headers.CacheControl.Extensions.Add(new NameValueHeaderValue(parts[0], parts[1]));
        }

        return response;
    }

    private static string? XCache(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Cache", out var values) ? values.FirstOrDefault() : null;

    [Theory]
    [InlineData("max-age=0", false)]
    [InlineData("min-fresh=60", false)]
    [InlineData("max-stale=0", false)]
    [InlineData("max-age=0", true)]
    [InlineData("min-fresh=60", true)]
    [InlineData("max-stale=0", true)]
    public async Task StaleIfError_RespectsExplicitRequestFreshness(string directive, bool originThrows)
    {
        var (client, origin) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions { EnableStaleIfError = true }, timeProvider: _time);
        using var owner = client;
        origin.SetResponse(Response("cached", TimeSpan.FromSeconds(1), "stale-if-error=3600"));
        using var first = await client.GetAsync(Url);
        _time.Advance(TimeSpan.FromSeconds(2));
        origin.ShouldThrowException = originThrows;
        if (!originThrows) origin.SetResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.CacheControl = CacheControlHeaderValue.Parse(directive);
        if (originThrows) await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
        else
        {
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task ExpiredExpires_DoesNotFallBackToHeuristicOrDefaultFreshness(int seconds)
    {
        var options = new HttpCacheOptions { AllowHeuristicFreshness = true, DefaultMaxAge = TimeSpan.FromHours(1) };
        var (client, inner) = HttpCacheTestFactory.CreateClient(options, timeProvider: _time);
        using var owner = client;
        var expired = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("old") };
        expired.Headers.Date = _time.GetUtcNow();
        expired.Content.Headers.Expires = _time.GetUtcNow().AddSeconds(seconds);
        expired.Content.Headers.LastModified = _time.GetUtcNow().AddDays(-1);
        inner.SetResponses(expired, Response("new", TimeSpan.FromMinutes(1)));

        using var first = await client.GetAsync(Url);
        using var second = await client.GetAsync(Url);

        Assert.Equal("new", await second.Content.ReadAsStringAsync());
        Assert.Equal(2, inner.RequestCount);
    }

    [Fact]
    public void RawInvalidExpires_FromCustomStorage_IsImmediatelyStale()
    {
        var entry = new HttpCacheEntry
        {
            RequestUri = Url,
            Method = "GET",
            Date = _time.GetUtcNow(),
            StoredAt = _time.GetUtcNow(),
            ContentHeaders = new() { ["expires"] = new[] { "0" } },
            LastModified = _time.GetUtcNow().AddDays(-1)
        };
        var calculator = new FreshnessCalculator(new HttpCacheOptions { DefaultMaxAge = TimeSpan.FromHours(1) }, _time);
        Assert.Equal(TimeSpan.Zero, calculator.GetFreshnessLifetime(entry));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("not a date")]
    public async Task InvalidExpires_DoesNotFallBackToHeuristicOrDefaultFreshness(string expires)
    {
        var options = new HttpCacheOptions { AllowHeuristicFreshness = true, DefaultMaxAge = TimeSpan.FromHours(1) };
        var (client, inner) = HttpCacheTestFactory.CreateClient(options, timeProvider: _time);
        using var owner = client;
        var expired = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("old") };
        expired.Headers.Date = _time.GetUtcNow();
        expired.Content.Headers.TryAddWithoutValidation("Expires", expires);
        expired.Content.Headers.LastModified = _time.GetUtcNow().AddDays(-1);
        inner.SetResponses(expired, Response("new", TimeSpan.FromMinutes(1)));

        using var first = await client.GetAsync(Url);
        using var second = await client.GetAsync(Url);

        Assert.Equal("new", await second.Content.ReadAsStringAsync());
        Assert.Equal(2, inner.RequestCount);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("not a date")]
    [InlineData("")]
    public void Revalidation_InvalidExpiresReplacement_DoesNotRetainPriorFreshness(string expires)
    {
        var entry = new HttpCacheEntry
        {
            RequestUri = Url,
            Method = "GET",
            Date = _time.GetUtcNow(),
            StoredAt = _time.GetUtcNow(),
            Expires = _time.GetUtcNow().AddHours(1)
        };
        using var response = new HttpResponseMessage(HttpStatusCode.NotModified) { Content = new StringContent("") };
        response.Headers.Date = _time.GetUtcNow();
        response.Content.Headers.TryAddWithoutValidation("Expires", new[] { expires });
        Assert.True(response.Content.Headers.Contains("Expires"));
        var updated = entry.WithUpdatedHeaders(response, _time.GetUtcNow());
        var calculator = new FreshnessCalculator(new HttpCacheOptions { DefaultMaxAge = TimeSpan.FromHours(1) }, _time);
        Assert.Equal(TimeSpan.Zero, calculator.GetFreshnessLifetime(updated));
    }

    [Fact]
    public void Revalidation_AbsentExpires_PreservesPriorFreshness()
    {
        var entry = new HttpCacheEntry { RequestUri = Url, Method = "GET", Expires = _time.GetUtcNow().AddHours(1) };
        using var response = new HttpResponseMessage(HttpStatusCode.NotModified);
        Assert.Equal(entry.Expires, entry.WithUpdatedHeaders(response, _time.GetUtcNow()).Expires);
    }

    [Fact]
    public async Task MaxAge_IsFreshOneTickBefore_AndStaleExactlyAtIt()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions { AddDiagnosticHeaders = true }, timeProvider: _time);
        inner.SetResponses(Response("v1", TimeSpan.FromSeconds(60)), Response("v2", TimeSpan.FromSeconds(60)));

        await client.GetAsync(Url);

        _time.Advance(TimeSpan.FromSeconds(60) - OneTick);
        var fresh = await client.GetAsync(Url);
        Assert.Equal("HIT", XCache(fresh));
        Assert.Equal("v1", await fresh.Content.ReadAsStringAsync());

        _time.Advance(OneTick);
        var refetched = await client.GetAsync(Url);
        Assert.Equal("v2", await refetched.Content.ReadAsStringAsync());
        Assert.Equal(2, inner.RequestCount);
    }

    [Fact]
    public async Task StaleWhileRevalidate_ServesStaleUntilOneTickBeforeTheWindowEnds()
    {
        var options = new HttpCacheOptions { EnableStaleWhileRevalidate = true, AddDiagnosticHeaders = true };
        var state = new HttpCacheClientState("test");
        var (client, inner) = HttpCacheTestFactory.CreateClient(options, state, timeProvider: _time);
        inner.SetResponses(
            Response("v1", TimeSpan.FromSeconds(60), "stale-while-revalidate=30"),
            Response("v2", TimeSpan.FromSeconds(60), "stale-while-revalidate=30"));

        await client.GetAsync(Url);

        _time.Advance(TimeSpan.FromSeconds(90) - OneTick);
        var stale = await client.GetAsync(Url);

        Assert.Equal("STALE", XCache(stale));
        Assert.Equal("v1", await stale.Content.ReadAsStringAsync());
        await HttpCacheTestFactory.WaitUntilAsync(() => state.RevalidationsInFlight.IsEmpty, "the background revalidation has finished");
        Assert.Equal(2, inner.RequestCount);
    }

    [Fact]
    public async Task StaleWhileRevalidate_GoesToTheOriginExactlyWhenTheWindowEnds()
    {
        var options = new HttpCacheOptions { EnableStaleWhileRevalidate = true, AddDiagnosticHeaders = true };
        var (client, inner) = HttpCacheTestFactory.CreateClient(options, timeProvider: _time);
        inner.SetResponses(
            Response("v1", TimeSpan.FromSeconds(60), "stale-while-revalidate=30"),
            Response("v2", TimeSpan.FromSeconds(60), "stale-while-revalidate=30"));

        await client.GetAsync(Url);

        _time.Advance(TimeSpan.FromSeconds(90));
        var response = await client.GetAsync(Url);

        Assert.Equal("MISS", XCache(response));
        Assert.Equal("v2", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)] // the stale-if-error directive sets the window
    [InlineData(true)]  // MaxStaleIfError sets it when the response has none
    public async Task StaleIfError_ServesStaleUntilOneTickBeforeTheWindowEnds(bool fromOptions)
    {
        var options = new HttpCacheOptions { EnableStaleIfError = true, AddDiagnosticHeaders = true, MaxStaleIfError = TimeSpan.FromSeconds(30) };
        var (client, inner) = HttpCacheTestFactory.CreateClient(options, timeProvider: _time);
        inner.SetResponse(Response("v1", TimeSpan.FromSeconds(60), fromOptions ? null : "stale-if-error=30"));

        await client.GetAsync(Url);
        inner.ShouldThrowException = true;

        _time.Advance(TimeSpan.FromSeconds(90) - OneTick);
        var stale = await client.GetAsync(Url);
        Assert.Equal("STALE-IF-ERROR", XCache(stale));
        Assert.Equal("v1", await stale.Content.ReadAsStringAsync());

        _time.Advance(OneTick);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(Url));
    }

    [Fact]
    public async Task BackgroundRevalidation_IsAbandonedExactlyAtItsTimeout_OnTheInjectedClock()
    {
        var options = new HttpCacheOptions
        {
            EnableStaleWhileRevalidate = true,
            AddDiagnosticHeaders = true,
            BackgroundRevalidationTimeout = TimeSpan.FromMinutes(1) // longer than the test waits on the real clock
        };
        var state = new HttpCacheClientState("test");
        using var origin = new HangingAfterFirstHandler(Response("v1", TimeSpan.FromSeconds(60), "stale-while-revalidate=300"));
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(options, origin, state, timeProvider: _time));

        await client.GetAsync(Url);
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("STALE", XCache(await client.GetAsync(Url)));
        await origin.SecondRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _time.Advance(TimeSpan.FromMinutes(1) - OneTick);
        Assert.False(origin.SecondRequestCancelled.Task.IsCompleted);
        Assert.Single(state.RevalidationsInFlight);

        _time.Advance(OneTick);
        await origin.SecondRequestCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await HttpCacheTestFactory.WaitUntilAsync(() => state.RevalidationsInFlight.IsEmpty, "the timed-out revalidation has been abandoned");
    }

    /// <summary>Answers the first request and holds the next one until it is cancelled.</summary>
    private sealed class HangingAfterFirstHandler(HttpResponseMessage first) : HttpMessageHandler
    {
        private int _requests;

        public TaskCompletionSource SecondRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondRequestCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requests) == 1)
            {
                return first;
            }

            SecondRequestStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                SecondRequestCancelled.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("unreachable");
        }
    }
}
