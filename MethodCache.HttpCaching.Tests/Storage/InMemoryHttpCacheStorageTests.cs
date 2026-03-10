using System.Collections.Generic;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MethodCache.HttpCaching.Options;
using MethodCache.HttpCaching.Storage;
using Xunit;

namespace MethodCache.HttpCaching.Tests.Storage;

public class InMemoryHttpCacheStorageTests
{
    [Fact]
    public async Task SetAsync_WithSizeLimitedMemoryCache_StoresEntrySuccessfully()
    {
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 });
        var options = new HttpCacheOptions();
        var storageOptions = new HttpCacheStorageOptions { MaxCacheSize = 1024, MaxResponseSize = 1024 };

        var storage = new InMemoryHttpCacheStorage(
            cache,
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Options.Options.Create(storageOptions),
            NullLogger<InMemoryHttpCacheStorage>.Instance);

        var entry = new HttpCacheEntry
        {
            RequestUri = "https://api.example.com/data",
            Method = "GET",
            StatusCode = System.Net.HttpStatusCode.OK,
            Content = new byte[64],
            Headers = new Dictionary<string, string[]>(),
            ContentHeaders = new Dictionary<string, string[]>(),
            StoredAt = DateTimeOffset.UtcNow
        };

        await storage.SetAsync("k", entry);
        var loaded = await storage.GetAsync("k");

        Assert.NotNull(loaded);
    }
}
