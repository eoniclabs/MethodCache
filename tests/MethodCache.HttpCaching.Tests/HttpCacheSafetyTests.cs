using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

/// <summary>Responses that must not be stored, shared or replayed as they arrived.</summary>
public class HttpCacheSafetyTests
{
    private const string Url = "https://api.example.com/items/1";

    [Fact]
    public async Task PartialContent_IsNeverStored()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        var partial = Cacheable("par");
        partial.StatusCode = HttpStatusCode.PartialContent;
        inner.SetResponses(partial, Cacheable("full"));

        using var range = new HttpRequestMessage(HttpMethod.Get, Url);
        range.Headers.Range = new RangeHeaderValue(0, 2);
        await client.SendAsync(range);

        Assert.Equal("full", await client.GetStringAsync(Url));
        Assert.Equal(2, inner.RequestCount);
    }

    [Fact]
    public async Task RangeRequest_GoesToTheOrigin_EvenWithAFullResponseCached()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        var partial = Cacheable("ful");
        partial.StatusCode = HttpStatusCode.PartialContent;
        inner.SetResponses(Cacheable("full"), partial);

        await client.GetStringAsync(Url);
        using var range = new HttpRequestMessage(HttpMethod.Get, Url);
        range.Headers.Range = new RangeHeaderValue(0, 2);
        var response = await client.SendAsync(range);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(2, inner.RequestCount);
    }

    [Fact]
    public async Task BodyWithoutLength_LargerThanTheLimit_IsStreamedToTheCaller_NotBuffered()
    {
        const int size = 4 * 1024 * 1024;
        var body = new CountingStream(size);
        var calls = 0;
        var handler = HttpCacheTestFactory.CreateHandler(
            new HttpCacheOptions { MaxResponseSize = 1024 },
            new LambdaHandler(_ =>
            {
                calls++;
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(calls == 1 ? body : new CountingStream(3)) };
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) };
                return response;
            }));
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead);
        Assert.True(body.BytesRead < 256 * 1024, $"read {body.BytesRead} bytes before the caller asked for the body");

        var received = (await response.Content.ReadAsByteArrayAsync()).Length;
        Assert.Equal(size, received);

        await client.GetAsync(Url);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PrivateCache_KeepsCookieAuthenticatedCallersApart()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        inner.SetResponses(Cacheable("alice's data"), Cacheable("bob's data"));

        Assert.Equal("alice's data", await GetWithCookie(client, "session=alice"));
        Assert.Equal("bob's data", await GetWithCookie(client, "session=bob"));
        Assert.Equal("alice's data", await GetWithCookie(client, "session=alice"));
        Assert.Equal(2, inner.RequestCount);
    }

    [Fact]
    public async Task HopByHopHeaders_AreNotReplayed()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        var response = Cacheable("v1");
        response.Headers.TryAddWithoutValidation("Keep-Alive", "timeout=5");
        response.Headers.Connection.Add("X-Hop");
        response.Headers.TryAddWithoutValidation("X-Hop", "1");
        response.Headers.TryAddWithoutValidation("X-End-To-End", "1");
        inner.SetResponses(response);

        await client.GetStringAsync(Url);
        var cached = await client.GetAsync(Url);

        Assert.Equal(1, inner.RequestCount);
        Assert.False(cached.Headers.Contains("Keep-Alive"));
        Assert.False(cached.Headers.Contains("Connection"));
        Assert.False(cached.Headers.Contains("X-Hop"));
        Assert.True(cached.Headers.Contains("X-End-To-End"));
    }

    [Fact]
    public async Task NotModified_UpdatesTheStoredContentHeaders()
    {
        var clock = new ManualTimeProvider();
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions(), timeProvider: clock);
        var first = Cacheable("v1", TimeSpan.FromSeconds(60));
        first.Headers.ETag = new EntityTagHeaderValue("\"e1\"");
        first.Content.Headers.Expires = clock.GetUtcNow().AddSeconds(60);
        var newExpires = clock.GetUtcNow().AddHours(1);
        var notModified = new HttpResponseMessage(HttpStatusCode.NotModified) { Content = new ByteArrayContent([]) };
        notModified.Headers.ETag = new EntityTagHeaderValue("\"e1\"");
        notModified.Content.Headers.Expires = newExpires;
        inner.SetResponses(first, notModified);

        await client.GetStringAsync(Url);
        clock.Advance(TimeSpan.FromMinutes(2));
        var revalidated = await client.GetAsync(Url);

        Assert.Equal(newExpires, revalidated.Content.Headers.Expires);
        Assert.Equal("v1", await revalidated.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RequestNoCache_RevalidatesAndStoresTheNewResponse()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        var first = Cacheable("v1");
        first.Headers.ETag = new EntityTagHeaderValue("\"e1\"");
        inner.SetResponses(first, Cacheable("v2"));

        await client.GetStringAsync(Url);
        using var noCache = new HttpRequestMessage(HttpMethod.Get, Url);
        noCache.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        var refreshed = await client.SendAsync(noCache);

        Assert.Equal("v2", await refreshed.Content.ReadAsStringAsync());
        Assert.Contains(inner.Requests[1].Headers.IfNoneMatch, tag => tag.Tag == "\"e1\"");
        Assert.Equal("v2", await client.GetStringAsync(Url));
        Assert.Equal(2, inner.RequestCount);
    }

    [Fact]
    public async Task ShortLivedVariant_DoesNotShortenTheUrlEntry()
    {
        var storage = new TimeToLiveStorage();
        var options = new HttpCacheOptions { IsSharedCache = true };
        options.KeyHeaders.Add("X-Tenant");
        var (client, inner) = HttpCacheTestFactory.CreateClient(options, storage);
        inner.SetResponses(Cacheable("a", TimeSpan.FromHours(1)), Cacheable("b", TimeSpan.FromSeconds(10)));

        await GetWithTenant(client, "a");
        await GetWithTenant(client, "b");

        var urlEntry = HttpCacheKeys(storage).Single(key => !key.Contains('|'));
        Assert.True(storage.TimeToLives[urlEntry] > TimeSpan.FromMinutes(59), $"URL entry kept for {storage.TimeToLives[urlEntry]}");
    }

    [Fact]
    public async Task ExpiresWithoutDate_IsFreshUntilItExpires()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("v1") };
        response.Content.Headers.Expires = DateTimeOffset.UtcNow.AddMinutes(10);
        inner.SetResponses(response);

        await client.GetStringAsync(Url);

        Assert.Equal("v1", await client.GetStringAsync(Url));
        Assert.Equal(1, inner.RequestCount);
    }

    [Fact]
    public async Task AnonymousHit_InAPrivateCache_ReadsTheStorageOnce()
    {
        var storage = new TimeToLiveStorage();
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions(), storage);
        inner.SetResponses(Cacheable("v1"));

        await client.GetStringAsync(Url);
        storage.Reads = 0;

        Assert.Equal("v1", await client.GetStringAsync(Url));
        Assert.Equal(1, storage.Reads);
    }

    [Fact]
    public async Task AnonymousAndAuthenticatedCallers_NeverSeeEachOthersResponses()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        inner.SetResponses(Cacheable("anonymous"), Cacheable("alice"), Cacheable("anonymous again"));

        Assert.Equal("anonymous", await client.GetStringAsync(Url));
        Assert.Equal("alice", await GetWithCookie(client, "session=alice"));
        Assert.Equal("alice", await GetWithCookie(client, "session=alice"));
        Assert.Equal("anonymous again", await client.GetStringAsync(Url));
        Assert.Equal("anonymous again", await client.GetStringAsync(Url));
        Assert.Equal("alice", await GetWithCookie(client, "session=alice"));
        Assert.Equal(3, inner.RequestCount);
    }

    [Fact]
    public async Task CallerConditionalRequest_GetsTheOrigins304_NotTheCachedBody()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        var first = Cacheable("v1");
        first.Headers.ETag = new EntityTagHeaderValue("\"e1\"");
        var notModified = new HttpResponseMessage(HttpStatusCode.NotModified) { Content = new ByteArrayContent([]) };
        notModified.Headers.ETag = new EntityTagHeaderValue("\"e1\"");
        inner.SetResponses(first, notModified);

        await client.GetStringAsync(Url);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, Url);
        conditional.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        conditional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"mine\""));
        var response = await client.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Contains(inner.Requests[1].Headers.IfNoneMatch, tag => tag.Tag == "\"mine\"");
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task NonDefaultSuccessStatus_IsStoredOnlyWithExplicitFreshness(bool maxAge, int expectedRequests)
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        HttpResponseMessage Accepted(string body)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent(body) };
            response.Headers.ETag = new EntityTagHeaderValue("\"e1\"");
            if (maxAge)
            {
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) };
            }

            return response;
        }

        inner.SetResponses(Accepted("a"), Accepted("b"));

        await client.GetStringAsync(Url);
        await client.GetStringAsync(Url);

        Assert.Equal(expectedRequests, inner.RequestCount);
    }

    private static IEnumerable<string> HttpCacheKeys(TimeToLiveStorage storage) => storage.TimeToLives.Keys;

    private static async Task<string> GetWithCookie(HttpClient client, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task GetWithTenant(HttpClient client, string tenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Add("X-Tenant", tenant);
        await client.SendAsync(request);
    }

    private static HttpResponseMessage Cacheable(string body, TimeSpan? maxAge = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = maxAge ?? TimeSpan.FromMinutes(10) };
        return response;
    }

    private sealed class LambdaHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    /// <summary>A body of <c>size</c> bytes with no known length, counting what was read.</summary>
    private sealed class CountingStream(long size) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, size - BytesRead);
            buffer.AsSpan(offset, n).Fill((byte)'x');
            BytesRead += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TimeToLiveStorage : IHttpCacheStorage
    {
        private readonly Dictionary<string, HttpCacheEntry> _entries = new();

        public Dictionary<string, TimeSpan> TimeToLives { get; } = new();

        public int Reads { get; set; }

        public ValueTask<HttpCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            Reads++;
            return ValueTask.FromResult(_entries.GetValueOrDefault(key));
        }

        public ValueTask SetAsync(string key, HttpCacheEntry entry, TimeSpan timeToLive, CancellationToken cancellationToken = default)
        {
            _entries[key] = entry;
            TimeToLives[key] = timeToLive;
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _entries.Remove(key);
            TimeToLives.Remove(key);
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync(CancellationToken cancellationToken = default)
        {
            _entries.Clear();
            TimeToLives.Clear();
            return ValueTask.CompletedTask;
        }
    }
}
