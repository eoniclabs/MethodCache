using MethodCache.TestSupport;
using System.Net;
using System.Net.Http.Headers;
using MethodCache.HttpCaching.Internal;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class InvalidationRaceTests
{
    private const string Url = "https://api.example.com/racing-write";

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task SuccessfulPost_DuringOlderOriginRequest_DoesNotRepublishOldResponse(bool distributed, bool variant, bool background)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var backend = distributed ? HttpEpochTestCache.Create(time) : null;
        using var firstStore = Store(time, backend);
        using var secondStore = distributed ? Store(time, backend) : null;
        using var origin = new GatedOrigin(background);
        var options = new HttpCacheOptions { EnableStaleWhileRevalidate = background };
        using var reader = new HttpClient(HttpCacheTestFactory.CreateHandler(options, origin, firstStore, time));
        using var writer = new HttpClient(HttpCacheTestFactory.CreateHandler(options, new WriteOrigin(), secondStore ?? firstStore, time));
        if (variant)
        {
            reader.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        }
        if (background)
        {
            using var seed = await reader.GetAsync(Url);
            time.Advance(TimeSpan.FromSeconds(2));
        }

        var pendingRead = reader.GetAsync(Url);
        await origin.Started.Task;
        using var write = await writer.PostAsync(Url, new StringContent("update"));
        origin.Release.TrySetResult();
        using var oldResponse = await pendingRead;
        if (background)
        {
            await origin.Completed.Task;
        }
        using var fresh = await reader.GetAsync(Url);

        Assert.Equal("new", await fresh.Content.ReadAsStringAsync());
        Assert.Equal(background ? 3 : 2, origin.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulPost_DuringStoragePublication_DoesNotExposeLateOldWrite(bool distributed)
    {
        var time = new FakeTimeProvider();
        var backend = distributed ? HttpEpochTestCache.Create(time) : null;
        using var storage = Store(time, backend);
        using var secondStore = distributed ? Store(time, backend) : null;
        var gated = new GatedStorage(storage);
        var options = new HttpCacheOptions();
        using var reader = new HttpClient(HttpCacheTestFactory.CreateHandler(options, new StaticOrigin("old"), gated, time));
        using var writer = new HttpClient(HttpCacheTestFactory.CreateHandler(options, new WriteOrigin(), secondStore ?? storage, time));
        var pending = reader.GetAsync(Url);
        await gated.Started.Task;
        using var post = await writer.PostAsync(Url, new StringContent("update"));
        gated.Release.TrySetResult();
        using var original = await pending;
        using var freshReader = new HttpClient(HttpCacheTestFactory.CreateHandler(options, new StaticOrigin("new"), secondStore ?? storage, time));
        using var fresh = await freshReader.GetAsync(Url);
        Assert.Equal("new", await fresh.Content.ReadAsStringAsync());
    }

    private static TestHttpStore Store(TimeProvider time, IDistributedCache? distributed = null)
        => new(time, distributed);

    private sealed class GatedOrigin(bool background) : HttpMessageHandler
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var read = Interlocked.Increment(ref _reads);
            var gatedRead = background ? 2 : 1;
            if (read == gatedRead)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            var response = new HttpResponseMessage(background && read == gatedRead ? HttpStatusCode.NotModified : HttpStatusCode.OK)
            {
                Content = background && read == gatedRead ? new CompletionContent(Completed) : new StringContent(read <= gatedRead ? "old" : "new"),
                Headers =
                {
                    ETag = new EntityTagHeaderValue("\"v1\""),
                    CacheControl = CacheControlHeaderValue.Parse(background && read == 1 ? "max-age=1, stale-while-revalidate=60" : "max-age=300")
                }
            };
            if (request.Headers.AcceptLanguage.Count > 0)
            {
                response.Headers.Vary.Add("Accept-Language");
            }
            return response;
        }
    }

    private sealed class CompletionContent(TaskCompletionSource completed) : ByteArrayContent([])
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) completed.TrySetResult();
        }
    }

    private sealed class WriteOrigin : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
    }

    private sealed class StaticOrigin(string value) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(value),
                Headers = { CacheControl = CacheControlHeaderValue.Parse("max-age=300") }
            });
    }

    private sealed class GatedStorage(IHttpCacheStorage inner) : IHttpCacheStorage
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<string> GetGenerationAsync(string key, CancellationToken cancellationToken = default) => inner.GetGenerationAsync(key, cancellationToken);
        public ValueTask InvalidateAsync(string key, CancellationToken cancellationToken = default) => inner.InvalidateAsync(key, cancellationToken);
        public ValueTask<HttpCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default) => inner.GetAsync(key, cancellationToken);
        public async ValueTask SetAsync(string key, HttpCacheEntry entry, TimeSpan timeToLive, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await inner.SetAsync(key, entry, timeToLive, cancellationToken);
        }
        public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) => inner.RemoveAsync(key, cancellationToken);
        public ValueTask ClearAsync(CancellationToken cancellationToken = default) => inner.ClearAsync(cancellationToken);
    }
}
