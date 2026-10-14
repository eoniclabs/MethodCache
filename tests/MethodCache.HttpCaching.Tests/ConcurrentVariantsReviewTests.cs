using System.Net;
using System.Net.Http.Headers;
using Xunit;
using MethodCache.HttpCaching.Internal;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MethodCache.HttpCaching.Tests;

public class ConcurrentVariantsReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentFirstVariants_BothRemainReachable(bool distributed)
    {
        var origin = new Origin();
        var backend = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        using var firstStore = new HttpCacheStore(Options.Create(new HttpCacheStorageOptions()), NullLogger<HttpCacheStore>.Instance, distributed: distributed ? backend : null);
        using var secondStore = new HttpCacheStore(Options.Create(new HttpCacheStorageOptions()), NullLogger<HttpCacheStore>.Instance, distributed: backend);
        var race = new Race();
        var storage = new RacingStorage(firstStore, race);
        using var firstClient = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions(), origin, storage));
        using var secondClient = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions(), origin,
            distributed ? new RacingStorage(secondStore, race) : storage));
        // Initialize the shared epoch before racing response metadata, as on an already running deployment.
        await firstStore.GetGenerationAsync(HttpCacheKeys.ForUri("test", HttpMethod.Get, new Uri("https://example.com/item")));
        var first = Read(firstClient, "sv");
        await race.FirstStoreWaiting.Task;
        var second = Read(secondClient, "en");
        await Task.WhenAll(first, second);

        Assert.Equal("sv", await Read(firstClient, "sv"));
        Assert.Equal("en", await Read(secondClient, "en"));
        Assert.Equal(2, origin.Calls);

        static async Task<string> Read(HttpClient client, string language)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/item");
            request.Headers.AcceptLanguage.ParseAdd(language);
            using var response = await client.SendAsync(request);
            return await response.Content.ReadAsStringAsync();
        }
    }

    private sealed class Origin : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.Headers.AcceptLanguage.Single().Value) };
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) };
            response.Headers.Vary.Add("Accept-Language");
            return Task.FromResult(response);
        }
    }

    private sealed class Race
    {
        public int Reads;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstStoreWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RacingStorage(IHttpCacheStorage inner, Race race) : IHttpCacheStorage
    {

        public ValueTask<string> GetGenerationAsync(string key, CancellationToken cancellationToken = default) => inner.GetGenerationAsync(key, cancellationToken);
        public ValueTask InvalidateAsync(string key, CancellationToken cancellationToken = default) => inner.InvalidateAsync(key, cancellationToken);
        public ValueTask SetAsync(string key, HttpCacheEntry entry, TimeSpan timeToLive, CancellationToken cancellationToken = default) => inner.SetAsync(key, entry, timeToLive, cancellationToken);
        public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) => inner.RemoveAsync(key, cancellationToken);
        public ValueTask ClearAsync(CancellationToken cancellationToken = default) => inner.ClearAsync(cancellationToken);

        public async ValueTask<HttpCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            var entry = await inner.GetAsync(key, cancellationToken);
            var read = Interlocked.Increment(ref race.Reads);
            if (read == 2)
            {
                race.FirstStoreWaiting.SetResult();
                await race.Release.Task;
            }
            if (read == 4) race.Release.SetResult();
            return entry;
        }
    }
}
