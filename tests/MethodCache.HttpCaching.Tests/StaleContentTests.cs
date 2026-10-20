using System.Net;
using System.Net.Http.Headers;
using MethodCache.HttpCaching.Internal;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class StaleContentTests
{
    [Fact]
    public async Task StaleWhileRevalidate_ServesCachedContent_WhenEnabled()
    {
        var options = new HttpCacheOptions();
        options.EnableStaleWhileRevalidate = true;
        options.AddDiagnosticHeaders = true;
        options.DefaultMaxAge = null;

        var state = new HttpCacheClientState("test");
        var (client, innerHandler) = HttpCacheTestFactory.CreateClient(options, state);

        // Stale for two minutes, inside the 300 second stale-while-revalidate window
        var pastDate = DateTimeOffset.UtcNow.AddMinutes(-7);
        var initialResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("original content"),
            Headers =
            {
                CacheControl = new CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromMinutes(5),
                    Extensions = { new NameValueHeaderValue("stale-while-revalidate", "300") }
                },
                Date = pastDate
            }
        };

        var revalidationResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("updated content"),
            Headers = { CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) } }
        };

        innerHandler.SetResponses(initialResponse, revalidationResponse);

        var response1 = await client.GetAsync("https://api.example.com/data");
        var content1 = await response1.Content.ReadAsStringAsync();

        var response2 = await client.GetAsync("https://api.example.com/data");
        var content2 = await response2.Content.ReadAsStringAsync();

        await HttpCacheTestFactory.WaitUntilAsync(() => state.RevalidationsInFlight.IsEmpty, "the background revalidation has finished");
        var response3 = await client.GetAsync("https://api.example.com/data");

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal("original content", content1);
        Assert.Equal("original content", content2);
        Assert.Matches(HttpCacheTestFactory.StaleWhileRevalidateHit, HttpCacheTestFactory.CacheStatus(response2));
        Assert.Equal(2, innerHandler.RequestCount);
        Assert.Equal("updated content", await response3.Content.ReadAsStringAsync());
        Assert.Matches(HttpCacheTestFactory.FreshHit, HttpCacheTestFactory.CacheStatus(response3));
    }

    [Fact]
    public async Task StaleIfError_ServesCachedContent_WhenRequestFails()
    {
        var options = new HttpCacheOptions();
        options.EnableStaleIfError = true;
        options.MaxStaleIfError = TimeSpan.FromHours(1);
        options.AddDiagnosticHeaders = true;

        var (client, innerHandler) = HttpCacheTestFactory.CreateClient(options);

        var pastDate = DateTimeOffset.UtcNow.AddMinutes(-10);
        var initialResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("cached content"),
            Headers =
            {
                CacheControl = new CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromMinutes(5),
                    Extensions = { new NameValueHeaderValue("stale-if-error", "3600") }
                },
                Date = pastDate
            }
        };

        innerHandler.SetResponse(initialResponse);

        var response1 = await client.GetAsync("https://api.example.com/data");
        var content1 = await response1.Content.ReadAsStringAsync();

        innerHandler.ShouldThrowException = true;

        var response2 = await client.GetAsync("https://api.example.com/data");
        var content2 = await response2.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal("cached content", content1);
        Assert.Equal("cached content", content2);
        Assert.Matches(HttpCacheTestFactory.StaleIfOriginFailed, HttpCacheTestFactory.CacheStatus(response2));
    }

    [Fact]
    public async Task StaleWhileRevalidate_Disabled_MakesOriginRequest()
    {
        var options = new HttpCacheOptions();
        options.EnableStaleWhileRevalidate = false;

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var (client, innerHandler) = HttpCacheTestFactory.CreateClient(options, timeProvider: time);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("content"),
            Headers =
            {
                CacheControl = new CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromMilliseconds(1),
                    Extensions = { new NameValueHeaderValue("stale-while-revalidate", "300") }
                }
            }
        };

        innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        var response1 = await client.GetAsync("https://api.example.com/data");
        time.Advance(TimeSpan.FromMilliseconds(1));
        var response2 = await client.GetAsync("https://api.example.com/data");

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(2, innerHandler.RequestCount);
    }

    [Fact]
    public async Task StaleIfError_Disabled_ThrowsException_WhenRequestFails()
    {
        var options = new HttpCacheOptions();
        options.EnableStaleIfError = false;

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var (client, innerHandler) = HttpCacheTestFactory.CreateClient(options, timeProvider: time);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("content"),
            Headers = { CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) } }
        };

        innerHandler.SetResponse(response);

        var response1 = await client.GetAsync("https://api.example.com/data");
        time.Advance(TimeSpan.FromSeconds(1));
        innerHandler.ShouldThrowException = true;

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://api.example.com/data"));
    }

    [Fact]
    public async Task StaleIfError_WithinTimeLimit_ServesCachedContent()
    {
        var options = new HttpCacheOptions();
        options.EnableStaleIfError = true;
        options.MaxStaleIfError = TimeSpan.FromSeconds(30);
        options.AddDiagnosticHeaders = true;

        var (client, innerHandler) = HttpCacheTestFactory.CreateClient(options);

        // Stale for ten seconds, inside the 30 second MaxStaleIfError
        var pastDate = DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(-10);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("stale content"),
            Headers =
            {
                CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) },
                Date = pastDate
            }
        };

        innerHandler.SetResponse(response);

        var response1 = await client.GetAsync("https://api.example.com/data");
        innerHandler.ShouldThrowException = true;
        var response2 = await client.GetAsync("https://api.example.com/data");
        var content = await response2.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal("stale content", content);
        Assert.Matches(HttpCacheTestFactory.StaleIfOriginFailed, HttpCacheTestFactory.CacheStatus(response2));
    }

    [Fact]
    public async Task StaleWhileRevalidate_ConcurrentRequests_QueuesSingleBackgroundRevalidation()
    {
        var options = new HttpCacheOptions();
        options.EnableStaleWhileRevalidate = true;
        options.AddDiagnosticHeaders = true;

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var state = new HttpCacheClientState("test");
        using var handler = new GatedOriginHandler(time);
        using var httpClient = new HttpClient(HttpCacheTestFactory.CreateHandler(options, handler, state, timeProvider: time));

        _ = await httpClient.GetAsync("https://api.example.com/concurrent-swr");
        time.Advance(TimeSpan.FromSeconds(1)); // past max-age, inside stale-while-revalidate

        // The revalidation is held open, so every concurrent request finds it still in flight
        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => httpClient.GetAsync("https://api.example.com/concurrent-swr")));

        Assert.All(responses, r => Assert.Matches(HttpCacheTestFactory.StaleWhileRevalidateHit, HttpCacheTestFactory.CacheStatus(r)));
        Assert.Single(state.RevalidationsInFlight);

        handler.Release();
        await HttpCacheTestFactory.WaitUntilAsync(() => state.RevalidationsInFlight.IsEmpty, "the background revalidation has finished");

        Assert.Equal(2, handler.RequestCount);
    }

    /// <summary>Answers the first request at once and holds every later one until <see cref="Release"/>.</summary>
    private sealed class GatedOriginHandler(TimeProvider time) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        public void Release() => _gate.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestNumber = Interlocked.Increment(ref _requestCount);

            if (requestNumber > 1)
            {
                await _gate.Task.WaitAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"payload-{requestNumber}"),
                Headers =
                {
                    Date = time.GetUtcNow(),
                    CacheControl = new CacheControlHeaderValue
                    {
                        MaxAge = TimeSpan.FromMilliseconds(1),
                        Extensions = { new NameValueHeaderValue("stale-while-revalidate", "300") }
                    }
                }
            };
        }
    }
}
