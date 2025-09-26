using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MethodCache.Core.Configuration;
using MethodCache.Core.Storage;
using MethodCache.HttpCaching.Options;
using MethodCache.HttpCaching.Storage;
using NSubstitute;
using Xunit;

namespace MethodCache.HttpCaching.Tests.Storage;

public class HybridHttpCacheStorageTests
{
    private readonly IStorageProvider _storageProvider = Substitute.For<IStorageProvider>();
    private readonly StorageOptions _storageOptions = new() { L2MaxExpiration = TimeSpan.FromHours(1) };
    private readonly HttpCacheOptions _httpOptions = new();

    private HybridHttpCacheStorage CreateStorage()
    {
        return new HybridHttpCacheStorage(
            _storageProvider,
            Microsoft.Extensions.Options.Options.Create(_httpOptions),
            Microsoft.Extensions.Options.Options.Create(new HttpCacheStorageOptions()),
            Microsoft.Extensions.Options.Options.Create(_storageOptions),
            NullLogger<HybridHttpCacheStorage>.Instance);
    }

    [Fact]
    public async Task SetAsync_SkipsWhenEntryTooLarge()
    {
        var storage = CreateStorage();
        var options = Microsoft.Extensions.Options.Options.Create(new HttpCacheStorageOptions { MaxResponseSize = 1 });
        var sut = new HybridHttpCacheStorage(
            _storageProvider,
            Microsoft.Extensions.Options.Options.Create(_httpOptions),
            options,
            Microsoft.Extensions.Options.Options.Create(_storageOptions),
            NullLogger<HybridHttpCacheStorage>.Instance);

        var entry = new HttpCacheEntry
        {
            RequestUri = "https://example.com",
            Method = "GET",
            StatusCode = System.Net.HttpStatusCode.OK,
            Content = new byte[10],
            Headers = new Dictionary<string, string[]>(),
            ContentHeaders = new Dictionary<string, string[]>(),
            StoredAt = DateTimeOffset.UtcNow
        };

        await sut.SetAsync("key", entry);
        await _storageProvider.DidNotReceive().SetAsync(
            Arg.Any<string>(),
            Arg.Any<HttpCacheEntry>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_StoresEntryWithExpiration()
    {
        var storage = CreateStorage();
        var entry = new HttpCacheEntry
        {
            RequestUri = "https://example.com",
            Method = "GET",
            StatusCode = System.Net.HttpStatusCode.OK,
            Content = "hello"u8.ToArray(),
            Headers = new Dictionary<string, string[]>(),
            ContentHeaders = new Dictionary<string, string[]>(),
            CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) },
            StoredAt = DateTimeOffset.UtcNow
        };

        await storage.SetAsync("key", entry);

        await _storageProvider.Received(1).SetAsync(
            "key",
            entry,
            Arg.Is<TimeSpan>(span => span <= TimeSpan.FromMinutes(5)),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveAsync_ForwardsToProvider()
    {
        var storage = CreateStorage();
        await storage.RemoveAsync("key");
        await _storageProvider.Received(1).RemoveAsync("key", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void GetStats_ReturnsCounts()
    {
        var storage = CreateStorage();
        storage.GetStats().StorageProviderName.Should().BeEmpty();
    }
}

