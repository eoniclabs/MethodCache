using System.Buffers;
using System.Net;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class BodyBufferTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LargeCacheableBody_AllocatesOneOwnedPayload(bool knownLength)
    {
        var bytes = Enumerable.Range(0, 1024 * 1024).Select(i => (byte)i).ToArray();
        // Other parallel HTTP tests can borrow Shared's warmed arrays; use an isolated pool for this bound.
        var pool = ArrayPool<byte>.Create(2 * 1024 * 1024, 4);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/item");
        using (var warmup = Response(bytes, knownLength))
            _ = await HttpCacheEntry.FromResponseAsync(request, warmup, DateTimeOffset.UnixEpoch, bytes.Length, bufferPool: pool);
        using var response = Response(bytes, knownLength);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var operation = HttpCacheEntry.FromResponseAsync(request, response, DateTimeOffset.UnixEpoch, bytes.Length, bufferPool: pool);
        Assert.True(operation.IsCompletedSuccessfully);
        var entry = await operation;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(entry);
        Assert.Equal(bytes, entry.Content);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.InRange(allocated, bytes.Length, bytes.Length + 32_768);
    }

    private static HttpResponseMessage Response(byte[] bytes, bool knownLength)
    {
        var content = new StreamContent(new BodyStream(bytes));
        if (knownLength) content.Headers.ContentLength = bytes.Length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(17, 40)]
    [InlineData(16_385, -1)]
    [InlineData(65_537, 65_536)]
    [InlineData(65_537, 65_537)]
    [InlineData(3_000_000, 3_000_000)]
    public async Task InaccurateLengthAndPartialReads_ReturnExactBodyAndReleaseTemporaryStorage(int size, int advertised)
    {
        var bytes = Enumerable.Range(0, size).Select(i => (byte)i).ToArray();
        var pool = new TrackingPool();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/item");
        using var body = new PartialBodyStream(bytes);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        if (advertised >= 0) response.Content.Headers.ContentLength = advertised;
        response.Content.Headers.TryAddWithoutValidation("X-Body", "preserved");

        var entry = await HttpCacheEntry.FromResponseAsync(request, response, DateTimeOffset.UnixEpoch, size + 100, bufferPool: pool);

        Assert.NotNull(entry);
        Assert.Equal(bytes, entry.Content);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("preserved", Assert.Single(response.Content.Headers.GetValues("X-Body")));
        Assert.Empty(pool.Outstanding);
        Assert.True(body.WasDisposed);
        // A subsequent renter can overwrite every returned array without corrupting the cached response.
        foreach (var returned in pool.Returned) Array.Fill(returned, (byte)231);
        Assert.Equal(bytes, entry.Content);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OversizedBody_StreamsEveryByteAndReturnsRentalOnDisposal(bool falseLength, bool readBody)
    {
        var bytes = Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray();
        var pool = new TrackingPool();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/item");
        var body = new PartialBodyStream(bytes);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        if (falseLength) response.Content.Headers.ContentLength = 1;

        Assert.Null(await HttpCacheEntry.FromResponseAsync(request, response, DateTimeOffset.UnixEpoch, 1024, bufferPool: pool));
        Assert.Equal(1025, body.BytesRead);
        Assert.Single(pool.Outstanding);
        Assert.False(body.WasDisposed);
        if (readBody) Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        response.Dispose();
        response.Dispose();
        Assert.Empty(pool.Outstanding);
        Assert.True(body.WasDisposed);
    }

    [Fact]
    public async Task AdvertisedOversizedBody_IsLeftUnreadForCaller()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/item");
        using var response = Response(new byte[2048], knownLength: true);
        var original = response.Content;
        var pool = new TrackingPool();
        Assert.Null(await HttpCacheEntry.FromResponseAsync(request, response, DateTimeOffset.UnixEpoch, 1024, bufferPool: pool));
        Assert.Same(original, response.Content);
        Assert.Empty(pool.Outstanding);
        Assert.Equal(2048, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [Theory]
    [InlineData("HEAD", HttpStatusCode.OK, 0)]
    [InlineData("GET", HttpStatusCode.NoContent, 0)]
    [InlineData("GET", HttpStatusCode.OK, 1000)]
    public async Task DeclaredLengthWithoutThatBody_DoesNotAllocateTheDeclaredLength(string method, HttpStatusCode status, int actual)
    {
        // A HEAD carries the GET's Content-Length with no body; a lying origin can declare any length up to the limit.
        const long declared = 8_000_000;
        var bytes = Enumerable.Range(0, actual).Select(i => (byte)i).ToArray();
        // An isolated pool, warmed below, so a pooled rental is not counted as an allocation.
        var pool = new TrackingPool(ArrayPool<byte>.Create(2 * 1024 * 1024, 4));
        using var request = new HttpRequestMessage(new HttpMethod(method), "https://example.test/item");
        using (var warmup = DeclaredResponse())
            _ = await HttpCacheEntry.FromResponseAsync(request, warmup, DateTimeOffset.UnixEpoch, 10 * 1024 * 1024, bufferPool: pool);
        using var response = DeclaredResponse();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var operation = HttpCacheEntry.FromResponseAsync(request, response, DateTimeOffset.UnixEpoch, 10 * 1024 * 1024, bufferPool: pool);
        Assert.True(operation.IsCompletedSuccessfully);
        var entry = await operation;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(entry);
        Assert.Equal(bytes, entry.Content);
        Assert.Empty(pool.Outstanding);
        Assert.InRange(allocated, 0, 128 * 1024);

        HttpResponseMessage DeclaredResponse()
        {
            var content = new StreamContent(new BodyStream(bytes));
            content.Headers.ContentLength = declared;
            return new HttpResponseMessage(status) { Content = content };
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRead_ReturnsRentalAndLeavesBodyOwnedByOriginalResponse(bool cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/item");
        var body = new InterruptedStream(cancel);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        var original = response.Content;
        var pool = new TrackingPool();
        using var cancellation = new CancellationTokenSource();
        var read = HttpCacheEntry.FromResponseAsync(request, response, DateTimeOffset.UnixEpoch, 1024, cancellation.Token, pool);
        if (cancel)
        {
            await body.Started.Task;
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        }
        else await Assert.ThrowsAsync<IOException>(() => read);
        Assert.Empty(pool.Outstanding);
        Assert.Same(original, response.Content);
        Assert.False(body.WasDisposed);
        response.Dispose();
        Assert.True(body.WasDisposed);
    }

    /// <summary>Tracks rentals; with <paramref name="inner"/>, reuses that pool's arrays so allocations can be measured.</summary>
    private sealed class TrackingPool(ArrayPool<byte>? inner = null) : ArrayPool<byte>
    {
        internal HashSet<byte[]> Outstanding { get; } = new();
        internal List<byte[]> Returned { get; } = new();
        public override byte[] Rent(int minimumLength)
        {
            var rental = inner?.Rent(minimumLength) ?? new byte[minimumLength];
            Assert.True(Outstanding.Add(rental));
            return rental;
        }
        public override void Return(byte[] array, bool clearArray = false)
        {
            Assert.True(clearArray);
            Assert.True(Outstanding.Remove(array));
            Array.Clear(array);
            Returned.Add(array);
            inner?.Return(array);
        }
    }

    private sealed class PartialBodyStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
        internal bool WasDisposed { get; private set; }
        internal int BytesRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = Read(buffer.Span[..Math.Min(buffer.Length, 137)]);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
        protected override void Dispose(bool disposing) { WasDisposed |= disposing; base.Dispose(disposing); }
    }

    private sealed class InterruptedStream(bool cancel) : MemoryStream
    {
        public override bool CanSeek => false;
        internal bool WasDisposed { get; private set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            if (!cancel) throw new IOException("body interrupted");
            await new TaskCompletionSource().Task.WaitAsync(cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing) { WasDisposed |= disposing; base.Dispose(disposing); }
    }

    private sealed class BodyStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
    }
}
