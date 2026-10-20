using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class RequestMetadataReviewTests
{
    [Fact]
    public async Task BackgroundRevalidation_PreservesProtocolAndRequestOptions()
    {
        var time = new FakeTimeProvider();
        var key = new HttpRequestOptionsKey<string>("transport-context");
        var seen = new TaskCompletionSource<(Version Version, HttpVersionPolicy Policy, string? Context)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions { EnableStaleWhileRevalidate = true, AddDiagnosticHeaders = true }, new CaptureHandler(time, key, seen), timeProvider: time));
        HttpRequestMessage Request()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/data") { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            request.Options.Set(key, "tenant-a");
            return request;
        }
        using var first = Request();
        using var initial = await client.SendAsync(first);
        time.Advance(TimeSpan.FromSeconds(2));
        using var second = Request();
        using var stale = await client.SendAsync(second);
        Assert.Matches(HttpCacheTestFactory.StaleWhileRevalidateHit, HttpCacheTestFactory.CacheStatus(stale));
        var metadata = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((HttpVersion.Version20, HttpVersionPolicy.RequestVersionExact, "tenant-a"), metadata);
    }

    private sealed class CaptureHandler(FakeTimeProvider time, HttpRequestOptionsKey<string> key, TaskCompletionSource<(Version, HttpVersionPolicy, string?)> seen) : HttpMessageHandler
    {
        private int _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) > 1)
            {
                request.Options.TryGetValue(key, out var context);
                seen.TrySetResult((request.Version, request.VersionPolicy, context));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data"),
                Headers = { Date = time.GetUtcNow(), CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1), Extensions = { new NameValueHeaderValue("stale-while-revalidate", "60") } } }
            });
        }
    }
}
