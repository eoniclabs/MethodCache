using System.Net;
using System.Net.Http.Headers;
using Xunit;
using NSubstitute;
using Microsoft.Extensions.Time.Testing;
using MethodCache.HttpCaching.Internal;

namespace MethodCache.HttpCaching.Tests;

public class ContentOwnershipTests
{
    [Fact]
    public async Task OversizedBody_ContentOwnerDoesNotCloseStream_ClosesStreamExplicitly()
    {
        using var body = new MemoryStream(new byte[1000]);
        var original = new NonClosingContent(body);
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions { MaxResponseSize = 100 }, new Origin(Fresh(original))));
        var response = await client.GetAsync("https://example.test/content", HttpCompletionOption.ResponseHeadersRead);
        Assert.True(body.CanRead);
        response.Dispose();
        Assert.False(body.CanRead);
        Assert.Equal(1, original.Disposals);
    }

    private sealed class NonClosingContent(Stream body) : HttpContent
    {
        public int Disposals;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context) => body.CopyToAsync(destination);
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(body);
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; }
    }

    [Theory]
    [InlineData(100, 1000)]
    [InlineData(1000, 100)]
    public async Task BufferedOrStreamedBody_DisposesOriginalContentOnce(int size, int limit)
    {
        var original = new OwnedContent(new MemoryStream(new byte[size]));
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(
            new HttpCacheOptions { MaxResponseSize = limit }, new Origin(Fresh(original))));
        var response = await client.GetAsync("https://example.test/content", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(new byte[size], await response.Content.ReadAsByteArrayAsync());
        response.Dispose();
        Assert.Equal(1, original.Disposals);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BodyReadFailure_DisposesUpstreamAndPropagatesFailure(bool cancel)
    {
        using var stop = new CancellationTokenSource();
        var original = new OwnedContent(new FailingBody(stop, cancel));
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions(), new Origin(Fresh(original))));

        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("https://example.test/content", stop.Token));
        else
            await Assert.ThrowsAnyAsync<IOException>(() => client.GetAsync("https://example.test/content", HttpCompletionOption.ResponseHeadersRead));

        Assert.Equal(1, original.Disposals);
    }

    [Fact]
    public async Task RevalidationStorageFailure_ServesTheRevalidatedBodyAndDisposesNotModifiedResponse()
    {
        var clock = new FakeTimeProvider();
        var backing = HttpCacheTestFactory.CreateInMemoryStorage(timeProvider: clock);
        var storage = Substitute.For<IHttpCacheStorage>();
        var fail = false;
        storage.GetGenerationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(c => backing.GetGenerationAsync(c.ArgAt<string>(0), c.ArgAt<CancellationToken>(1)));
        storage.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(c => backing.GetAsync(c.ArgAt<string>(0), c.ArgAt<CancellationToken>(1)));
        storage.SetAsync(Arg.Any<string>(), Arg.Any<HttpCacheEntry>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(c => fail ? ValueTask.FromException(new InvalidOperationException("storage failed")) :
                backing.SetAsync(c.ArgAt<string>(0), c.ArgAt<HttpCacheEntry>(1), c.ArgAt<TimeSpan>(2), c.ArgAt<CancellationToken>(3)));
        var first = Fresh(new StringContent("cached"));
        first.Headers.CacheControl!.MaxAge = TimeSpan.FromSeconds(1);
        first.Headers.ETag = new EntityTagHeaderValue("\"version\"");
        var original = new OwnedContent(new MemoryStream());
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions(),
            new Origin(first, new HttpResponseMessage(HttpStatusCode.NotModified) { Content = original }), storage, clock));
        using var cached = await client.GetAsync("https://example.test/content");
        clock.Advance(TimeSpan.FromSeconds(2));
        fail = true;

        // The origin answered; a cache that can't be updated must not fail the request
        using var revalidated = await client.GetAsync("https://example.test/content");

        Assert.Equal("cached", await revalidated.Content.ReadAsStringAsync());
        Assert.Equal(1, original.Disposals);
    }

    [Fact]
    public async Task RevalidationToNoStore_InvalidationFails_FailsTheRequest()
    {
        var clock = new FakeTimeProvider();
        var backing = HttpCacheTestFactory.CreateInMemoryStorage(timeProvider: clock);
        var storage = Substitute.For<IHttpCacheStorage>();
        storage.GetGenerationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(c => backing.GetGenerationAsync(c.ArgAt<string>(0), c.ArgAt<CancellationToken>(1)));
        storage.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(c => backing.GetAsync(c.ArgAt<string>(0), c.ArgAt<CancellationToken>(1)));
        storage.SetAsync(Arg.Any<string>(), Arg.Any<HttpCacheEntry>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(c => backing.SetAsync(c.ArgAt<string>(0), c.ArgAt<HttpCacheEntry>(1), c.ArgAt<TimeSpan>(2), c.ArgAt<CancellationToken>(3)));
        storage.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException(new InvalidOperationException("storage failed")));
        var first = Fresh(new StringContent("cached"));
        first.Headers.CacheControl!.MaxAge = TimeSpan.FromSeconds(1);
        first.Headers.ETag = new EntityTagHeaderValue("\"version\"");
        var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
        notModified.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions(),
            new Origin(first, notModified), storage, clock));
        using var cached = await client.GetAsync("https://example.test/content");
        clock.Advance(TimeSpan.FromSeconds(2));

        // The stored response may no longer be used; if it can't be removed, the caller must know
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://example.test/content"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForcedRevalidation_StorageFails_RemovesTheOldEntry(bool removalFails)
    {
        var clock = new FakeTimeProvider();
        var backing = HttpCacheTestFactory.CreateInMemoryStorage(timeProvider: clock);
        var storage = Substitute.For<IHttpCacheStorage>();
        var fail = false;
        storage.GetGenerationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(c => backing.GetGenerationAsync(c.ArgAt<string>(0), c.ArgAt<CancellationToken>(1)));
        storage.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(c => backing.GetAsync(c.ArgAt<string>(0), c.ArgAt<CancellationToken>(1)));
        storage.SetAsync(Arg.Any<string>(), Arg.Any<HttpCacheEntry>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(c => fail ? ValueTask.FromException(new InvalidOperationException("storage failed")) :
                backing.SetAsync(c.ArgAt<string>(0), c.ArgAt<HttpCacheEntry>(1), c.ArgAt<TimeSpan>(2), c.ArgAt<CancellationToken>(3)));
        storage.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(c => removalFails ? ValueTask.FromException(new InvalidOperationException("removal failed")) :
                backing.RemoveAsync(c.ArgAt<string>(0), c.ArgAt<CancellationToken>(1)));
        storage.InvalidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(c => backing.InvalidateAsync(c.ArgAt<string>(0), c.ArgAt<CancellationToken>(1)));
        var first = Fresh(new StringContent("cached"));
        first.Headers.ETag = new EntityTagHeaderValue("\"version\"");
        // The 304 changes the policy of a response that is still fresh: it now varies by caller
        var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
        notModified.Headers.Vary.Add("Authorization");
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions { IsSharedCache = true },
            new Origin(first, notModified, Fresh(new StringContent("new"))), storage, clock));
        using (await client.GetAsync("https://example.test/content")) { }
        fail = true;
        using var forced = new HttpRequestMessage(HttpMethod.Get, "https://example.test/content");
        forced.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        if (removalFails)
        {
            // The old entry can't be refreshed or removed, so the caller must know
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(forced));
            return;
        }

        using (await client.SendAsync(forced)) { }
        // The old entry, with the old Vary rules, is gone
        Assert.Equal("new", await client.GetStringAsync("https://example.test/content"));
    }

    [Fact]
    public async Task ForcedRevalidation_DistributedWriteFails_RemovesTheOldEntry()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var backend = new FailingDistributedCache();
        using var storage = new HttpCacheStore(
            Microsoft.Extensions.Options.Options.Create(new HttpCacheStorageOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpCacheStore>.Instance, clock, backend);
        var first = Fresh(new StringContent("cached"));
        first.Headers.ETag = new EntityTagHeaderValue("\"version\"");
        var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
        notModified.Headers.Vary.Add("Authorization");
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions { IsSharedCache = true },
            new Origin(first, notModified, Fresh(new StringContent("new"))), storage, clock));
        using (await client.GetAsync("https://example.test/content")) { }
        backend.FailWrites = true;
        using var forced = new HttpRequestMessage(HttpMethod.Get, "https://example.test/content");
        forced.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using (await client.SendAsync(forced)) { }
        backend.FailWrites = false;

        // The built-in distributed storage reports the failed write, so the entry with the old Vary rules is gone
        Assert.Equal("new", await client.GetStringAsync("https://example.test/content"));
    }

    private sealed class FailingDistributedCache : Microsoft.Extensions.Caching.Distributed.IDistributedCache
    {
        private readonly Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache _inner =
            new(Microsoft.Extensions.Options.Options.Create(new Microsoft.Extensions.Caching.Memory.MemoryDistributedCacheOptions()));
        public bool FailWrites { get; set; }
        // Generation records are written with a fixed key; let them through so only entry writes fail
        private bool Fails(string key) => FailWrites && !key.Contains("$generation:", StringComparison.Ordinal);
        public byte[]? Get(string key) => _inner.Get(key);
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => _inner.GetAsync(key, token);
        public void Refresh(string key) => _inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);
        public void Remove(string key) => _inner.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);
        public void Set(string key, byte[] value, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions options) => _inner.Set(key, value, options);
        public Task SetAsync(string key, byte[] value, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions options, CancellationToken token = default)
            => Fails(key) ? Task.FromException(new InvalidOperationException("backend down")) : _inner.SetAsync(key, value, options, token);
    }

    private static HttpResponseMessage Fresh(HttpContent content) => new(HttpStatusCode.OK)
    {
        Content = content,
        Headers = { CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(1) } }
    };
    private sealed class Origin(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _index;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(responses[_index++]);
    }
    private sealed class OwnedContent(Stream body) : HttpContent
    {
        public int Disposals;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context) => body.CopyToAsync(destination);
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(body);
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
    private sealed class FailingBody(CancellationTokenSource stop, bool cancel) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!cancel) return ValueTask.FromException<int>(new IOException("body failed"));
            stop.Cancel();
            return ValueTask.FromException<int>(new OperationCanceledException(cancellationToken));
        }
    }
}
