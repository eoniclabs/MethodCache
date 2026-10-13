using System.Net;
using System.Net.Http.Headers;
using MethodCache.HttpCaching.Internal;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MethodCache.HttpCaching.Tests.Storage;

public class HttpCacheStoreTests
{
    [Fact]
    public async Task SetAsync_WithSizeLimitedMemoryCache_StoresEntrySuccessfully()
    {
        using var storage = CreateStore(new HttpCacheStorageOptions { MaxCacheSize = 1024 });

        await storage.SetAsync("k", CreateEntry(), TimeSpan.FromMinutes(1));

        Assert.NotNull(await storage.GetAsync("k"));
    }

    [Fact]
    public async Task EntryWrittenByOneInstance_IsReadByAnother_ThroughTheDistributedCache()
    {
        var distributed = new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        using var first = CreateStore(distributed: distributed);
        using var second = CreateStore(distributed: distributed);
        var entry = CreateEntry();

        await first.SetAsync("k", entry, TimeSpan.FromMinutes(1));
        var loaded = await second.GetAsync("k");

        Assert.NotNull(loaded);
        Assert.Equal(entry.Content, loaded!.Content);
        Assert.Equal("\"v1\"", loaded.ETag);
        Assert.Equal(TimeSpan.FromMinutes(5), loaded.CacheControl!.MaxAge);
    }

    [Fact]
    public async Task RemoveAsync_RemovesFromTheDistributedCacheToo()
    {
        var distributed = new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        using var first = CreateStore(distributed: distributed);
        using var second = CreateStore(distributed: distributed);

        await first.SetAsync("k", CreateEntry(), TimeSpan.FromMinutes(1));
        Assert.NotNull(await second.GetAsync("k")); // second instance has read it once
        await first.RemoveAsync("k");

        Assert.Null(await second.GetAsync("k"));
    }

    [Fact]
    public async Task KeyPrefix_KeepsApplicationsSharingADistributedCacheApart()
    {
        var distributed = new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        using var orders = CreateStore(new HttpCacheStorageOptions { KeyPrefix = "orders:" }, distributed);
        using var billing = CreateStore(new HttpCacheStorageOptions { KeyPrefix = "billing:" }, distributed);

        await orders.SetAsync("k", CreateEntry(), TimeSpan.FromMinutes(1));

        Assert.Null(await billing.GetAsync("k"));
        Assert.NotNull(distributed.Get("orders:k"));
    }

    [Fact]
    public async Task FailingDistributedCache_ReportsWritesAndIgnoresReadsAndRemovals()
    {
        using var store = CreateStore(distributed: new ThrowingDistributedCache());

        // The handler decides what a failed write means (log it, or remove the entry it would have replaced)
        await Assert.ThrowsAnyAsync<Exception>(() => store.SetAsync("k", CreateEntry(), TimeSpan.FromMinutes(1)).AsTask());
        await store.RemoveAsync("k");

        Assert.Null(await store.GetAsync("k"));
    }

    [Fact]
    public void Serializer_RoundTripsEveryField()
    {
        var entry = CreateEntry() with
        {
            LastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Expires = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            Date = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            InitialAge = TimeSpan.FromSeconds(42),
            VaryHeaders = ["Accept-Language"],
            VariantGeneration = "g1"
        };
        var expiresAt = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);

        var (loaded, loadedExpiresAt) = HttpCacheEntrySerializer.Deserialize(HttpCacheEntrySerializer.Serialize(entry, expiresAt))!.Value;

        Assert.Equal(expiresAt, loadedExpiresAt);
        Assert.Equal(entry.RequestUri, loaded.RequestUri);
        Assert.Equal(entry.Method, loaded.Method);
        Assert.Equal(entry.StatusCode, loaded.StatusCode);
        Assert.Equal(entry.Content, loaded.Content);
        Assert.Equal(entry.Headers["X-Test"], loaded.Headers["x-test"]);
        Assert.Equal(entry.ContentHeaders["Content-Type"], loaded.ContentHeaders["Content-Type"]);
        Assert.Equal(entry.ETag, loaded.ETag);
        Assert.Equal(entry.LastModified, loaded.LastModified);
        Assert.Equal(entry.CacheControl!.ToString(), loaded.CacheControl!.ToString());
        Assert.Equal(entry.Expires, loaded.Expires);
        Assert.Equal(entry.Date, loaded.Date);
        Assert.Equal(entry.StoredAt, loaded.StoredAt);
        Assert.Equal(entry.InitialAge, loaded.InitialAge);
        Assert.Equal(entry.VaryHeaders, loaded.VaryHeaders);
        Assert.Equal(entry.VariantGeneration, loaded.VariantGeneration);
    }

    [Fact]
    public void Serializer_IgnoresUnknownFormat()
        => Assert.Null(HttpCacheEntrySerializer.Deserialize([99, 1, 2, 3]));

    private static HttpCacheStore CreateStore(HttpCacheStorageOptions? options = null, IDistributedCache? distributed = null)
        => new(Microsoft.Extensions.Options.Options.Create(options ?? new HttpCacheStorageOptions()), NullLogger<HttpCacheStore>.Instance, distributed: distributed);

    private static HttpCacheEntry CreateEntry() => new()
    {
        RequestUri = "https://api.example.com/data",
        Method = "GET",
        StatusCode = HttpStatusCode.OK,
        Content = "hello"u8.ToArray(),
        Headers = new Dictionary<string, string[]> { ["X-Test"] = ["1", "2"] },
        ContentHeaders = new Dictionary<string, string[]> { ["Content-Type"] = ["text/plain"] },
        ETag = "\"v1\"",
        CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5), Public = true },
        StoredAt = new DateTimeOffset(2026, 1, 1, 12, 0, 1, TimeSpan.Zero)
    };

    private sealed class ThrowingDistributedCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new InvalidOperationException("down");
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("down");
        public void Refresh(string key) => throw new InvalidOperationException("down");
        public Task RefreshAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("down");
        public void Remove(string key) => throw new InvalidOperationException("down");
        public Task RemoveAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("down");
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new InvalidOperationException("down");
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw new InvalidOperationException("down");
    }
}
