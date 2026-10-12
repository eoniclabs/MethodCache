using System.Net;
using System.Net.Http.Headers;
using Xunit;
using NSubstitute;
using Microsoft.Extensions.Time.Testing;

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
    public async Task RevalidationStorageFailure_DisposesNotModifiedResponse()
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

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://example.test/content"));

        Assert.Equal(1, original.Disposals);
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
