using System.Net;
using System.Net.Http.Headers;
using MethodCache.HttpCaching.Internal;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class SchemaRetirementReviewTests
{
    private const string Url = "https://example.com/schema";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Revalidation_ChangesSchema_RetiresOnceAndKeepsConcurrentNewPublication(bool notModified, bool background)
    {
        var clock = new ManualTimeProvider();
        using var origin = new SchemaOrigin(notModified);
        var storage = new RetirementStorage(HttpCacheTestFactory.CreateInMemoryStorage(timeProvider: clock));
        var options = new HttpCacheOptions { EnableStaleWhileRevalidate = background };
        var state = new HttpCacheClientState("test");
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(options, origin, state, storage, clock));
        using var concurrent = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions(), origin, storage, clock));
        Assert.Equal("old", await ReadAsync(client));
        clock.Advance(TimeSpan.FromSeconds(2));
        storage.AfterFirstRetirement = async () => Assert.Equal("fresh", await ReadAsync(concurrent));

        await ReadAsync(client);
        await WaitForBackgroundAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("fresh", await ReadAsync(concurrent));
        Assert.Equal(1, storage.Retirements);
        Assert.Equal(3, origin.Calls);

        async Task WaitForBackgroundAsync()
        {
            while (!state.RevalidationsInFlight.IsEmpty) await Task.Yield();
        }
    }

    private static async Task<string> ReadAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.AcceptLanguage.ParseAdd("sv");
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        using var response = await client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    private sealed class SchemaOrigin(bool notModified) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            var response = new HttpResponseMessage(call == 2 && notModified ? HttpStatusCode.NotModified : HttpStatusCode.OK)
            {
                Content = new StringContent(call == 1 ? "old" : call == 2 ? "replacement" : "fresh"),
                Headers =
                {
                    ETag = new EntityTagHeaderValue("\"schema\""),
                    CacheControl = CacheControlHeaderValue.Parse(call == 1 ? "max-age=1, stale-while-revalidate=60" : "max-age=300")
                }
            };
            response.Headers.Vary.Add(call == 1 ? "Accept-Language" : "Accept-Encoding");
            return Task.FromResult(response);
        }
    }

    private sealed class RetirementStorage(IHttpCacheStorage inner) : IHttpCacheStorage
    {
        private int _retirements;
        public int Retirements => Volatile.Read(ref _retirements);
        public Func<Task>? AfterFirstRetirement { get; set; }
        public ValueTask<string> GetGenerationAsync(string key, CancellationToken cancellationToken = default) => inner.GetGenerationAsync(key, cancellationToken);
        public ValueTask<HttpCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default) => inner.GetAsync(key, cancellationToken);
        public ValueTask SetAsync(string key, HttpCacheEntry entry, TimeSpan ttl, CancellationToken cancellationToken = default) => inner.SetAsync(key, entry, ttl, cancellationToken);
        public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) => inner.RemoveAsync(key, cancellationToken);
        public ValueTask ClearAsync(CancellationToken cancellationToken = default) => inner.ClearAsync(cancellationToken);
        public async ValueTask InvalidateAsync(string key, CancellationToken cancellationToken = default)
        {
            await inner.InvalidateAsync(key, cancellationToken);
            if (Interlocked.Increment(ref _retirements) == 1 && AfterFirstRetirement != null)
                await AfterFirstRetirement();
        }
    }
}
