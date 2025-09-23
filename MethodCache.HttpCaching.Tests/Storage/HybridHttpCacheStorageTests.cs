using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MethodCache.HttpCaching.Storage;
using MethodCache.Infrastructure.Abstractions;
using MethodCache.Infrastructure.Configuration;
using NSubstitute;
using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace MethodCache.HttpCaching.Tests.Storage;

public class HybridHttpCacheStorageTests
{
    private readonly IStorageProvider _storageProvider;
    private readonly HybridHttpCacheStorage _storage;
    private readonly HttpCacheOptions _httpOptions;
    private readonly StorageOptions _storageOptions;

    public HybridHttpCacheStorageTests()
    {
        _storageProvider = Substitute.For<IStorageProvider>();

        _httpOptions = new HttpCacheOptions
        {
            MaxResponseSize = 1024 * 1024, // 1MB
            DefaultMaxAge = TimeSpan.FromMinutes(5),
            AllowHeuristicFreshness = true,
            MaxHeuristicFreshness = TimeSpan.FromHours(1)
        };

        _storageOptions = new StorageOptions
        {
            L2MaxExpiration = TimeSpan.FromHours(24)
        };

        _storage = new HybridHttpCacheStorage(
            _storageProvider,
            Options.Create(_httpOptions),
            Options.Create(_storageOptions),
            NullLogger<HybridHttpCacheStorage>.Instance);
    }

    [Fact]
    public async Task GetAsync_WhenEntryExists_ReturnsEntry()
    {
        // Arrange
        const string key = "test-key";
        var entry = CreateTestEntry();
        _storageProvider.GetAsync<HttpCacheEntry>(key, Arg.Any<CancellationToken>()).Returns(entry);

        // Act
        var result = await _storage.GetAsync(key);

        // Assert
        result.Should().Be(entry);
        await _storageProvider.Received(1).GetAsync<HttpCacheEntry>(key, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_WhenEntryDoesNotExist_ReturnsNull()
    {
        // Arrange
        const string key = "test-key";
        _storageProvider.GetAsync<HttpCacheEntry>(key, Arg.Any<CancellationToken>()).Returns((HttpCacheEntry?)null);

        // Act
        var result = await _storage.GetAsync(key);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WhenEntryExceedsSizeLimit_RemovesAndReturnsNull()
    {
        // Arrange
        const string key = "test-key";
        var largeEntry = CreateTestEntryWithContent(new byte[2 * 1024 * 1024]); // 2MB > 1MB limit

        _storageProvider.GetAsync<HttpCacheEntry>(key, Arg.Any<CancellationToken>()).Returns(largeEntry);

        // Act
        var result = await _storage.GetAsync(key);

        // Assert
        result.Should().BeNull();
        await _storageProvider.Received(1).RemoveAsync(key, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_WithValidEntry_StoresWithCorrectExpiration()
    {
        // Arrange
        const string key = "test-key";
        var entry = CreateTestEntryWithMaxAge(TimeSpan.FromMinutes(10));

        // Act
        await _storage.SetAsync(key, entry);

        // Assert
        await _storageProvider.Received(1).SetAsync(
            key,
            entry,
            Arg.Is<TimeSpan>(exp => exp <= TimeSpan.FromMinutes(10) && exp >= TimeSpan.FromMinutes(9)),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_WithOversizedEntry_DoesNotStore()
    {
        // Arrange
        const string key = "test-key";
        var largeEntry = CreateTestEntryWithContent(new byte[2 * 1024 * 1024]); // 2MB > 1MB limit

        // Act
        await _storage.SetAsync(key, largeEntry);

        // Assert
        await _storageProvider.DidNotReceive().SetAsync(
            Arg.Any<string>(),
            Arg.Any<HttpCacheEntry>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_GeneratesCorrectTags()
    {
        // Arrange
        const string key = "test-key";
        var entry = CreateTestEntry();

        // Act
        await _storage.SetAsync(key, entry);

        // Assert
        await _storageProvider.Received(1).SetAsync(
            key,
            entry,
            Arg.Any<TimeSpan>(),
            Arg.Is<IEnumerable<string>>(tags => ValidateTags(tags)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveAsync_CallsStorageProvider()
    {
        // Arrange
        const string key = "test-key";

        // Act
        await _storage.RemoveAsync(key);

        // Assert
        await _storageProvider.Received(1).RemoveAsync(key, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearAsync_RemovesByTag()
    {
        // Act
        await _storage.ClearAsync();

        // Assert
        await _storageProvider.Received(1).RemoveByTagAsync("http-cache", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidateByUriAsync_RemovesByUriTag()
    {
        // Arrange
        const string uriPattern = "/api/users";

        // Act
        await _storage.InvalidateByUriAsync(uriPattern);

        // Assert
        await _storageProvider.Received(1).RemoveByTagAsync($"uri:{uriPattern}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidateByMethodAsync_RemovesByMethodTag()
    {
        // Arrange
        const string method = "POST";

        // Act
        await _storage.InvalidateByMethodAsync(method);

        // Assert
        await _storageProvider.Received(1).RemoveByTagAsync("method:POST", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void GetStats_ReturnsCorrectStatistics()
    {
        // Arrange
        _storageProvider.Name.Returns("TestProvider");

        // Act
        var stats = _storage.GetStats();

        // Assert
        stats.Should().NotBeNull();
        stats.StorageProviderName.Should().Be("TestProvider");
        stats.HitRatio.Should().Be(0.0); // No requests yet
    }

    [Theory]
    [InlineData(5)] // 5 minutes max-age
    [InlineData(60)] // 1 hour max-age
    [InlineData(1440)] // 24 hours max-age (should be clamped)
    public async Task SetAsync_ClampsExpirationToStorageMaximum(int maxAgeMinutes)
    {
        // Arrange
        const string key = "test-key";
        var entry = CreateTestEntryWithMaxAge(TimeSpan.FromMinutes(maxAgeMinutes));

        // Act
        await _storage.SetAsync(key, entry);

        // Assert
        await _storageProvider.Received(1).SetAsync(
            key,
            entry,
            Arg.Is<TimeSpan>(exp => exp <= _storageOptions.L2MaxExpiration),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_WithExpiresHeader_UsesExpiresForExpiration()
    {
        // Arrange
        const string key = "test-key";
        var futureTime = DateTimeOffset.UtcNow.AddMinutes(30);
        var entry = CreateTestEntryWithExpires(futureTime);

        // Act
        await _storage.SetAsync(key, entry);

        // Assert
        await _storageProvider.Received(1).SetAsync(
            key,
            entry,
            Arg.Is<TimeSpan>(exp => exp > TimeSpan.FromMinutes(25) && exp <= TimeSpan.FromMinutes(35)),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_WithHeuristicFreshness_UsesLastModifiedCalculation()
    {
        // Arrange
        const string key = "test-key";
        var lastModified = DateTimeOffset.UtcNow.AddDays(-10);
        var entry = CreateTestEntryWithLastModified(lastModified);

        // Act
        await _storage.SetAsync(key, entry);

        // Assert
        // Should use heuristic freshness (10% of last-modified age = ~1 day, but clamped to max)
        await _storageProvider.Received(1).SetAsync(
            key,
            entry,
            Arg.Is<TimeSpan>(exp => exp <= _httpOptions.MaxHeuristicFreshness),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_WithNoExplicitCacheDirectives_UsesDefaultMaxAge()
    {
        // Arrange
        const string key = "test-key";
        var entry = CreateTestEntryWithNoCacheDirectives();

        // Act
        await _storage.SetAsync(key, entry);

        // Assert
        await _storageProvider.Received(1).SetAsync(
            key,
            entry,
            _httpOptions.DefaultMaxAge!.Value,
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }

    private static HttpCacheEntry CreateTestEntry()
    {
        return new HttpCacheEntry
        {
            RequestUri = "https://api.example.com/users",
            Method = "GET",
            StatusCode = HttpStatusCode.OK,
            Content = "test content"u8.ToArray(),
            Headers = new Dictionary<string, string[]>(),
            ContentHeaders = new Dictionary<string, string[]>
            {
                ["Content-Type"] = new[] { "application/json; charset=utf-8" }
            },
            ETag = "\"abc123\"",
            Date = DateTimeOffset.UtcNow,
            StoredAt = DateTimeOffset.UtcNow
        };
    }

    private static HttpCacheEntry CreateTestEntryWithMaxAge(TimeSpan maxAge)
    {
        return new HttpCacheEntry
        {
            RequestUri = "https://api.example.com/users",
            Method = "GET",
            StatusCode = HttpStatusCode.OK,
            Content = "test content"u8.ToArray(),
            Headers = new Dictionary<string, string[]>(),
            ContentHeaders = new Dictionary<string, string[]>
            {
                ["Content-Type"] = new[] { "application/json; charset=utf-8" }
            },
            ETag = "\"abc123\"",
            Date = DateTimeOffset.UtcNow,
            StoredAt = DateTimeOffset.UtcNow,
            CacheControl = new CacheControlHeaderValue { MaxAge = maxAge }
        };
    }

    private static HttpCacheEntry CreateTestEntryWithContent(byte[] content)
    {
        return new HttpCacheEntry
        {
            RequestUri = "https://api.example.com/users",
            Method = "GET",
            StatusCode = HttpStatusCode.OK,
            Content = content,
            Headers = new Dictionary<string, string[]>(),
            ContentHeaders = new Dictionary<string, string[]>
            {
                ["Content-Type"] = new[] { "application/json; charset=utf-8" }
            },
            ETag = "\"abc123\"",
            Date = DateTimeOffset.UtcNow,
            StoredAt = DateTimeOffset.UtcNow
        };
    }

    private static HttpCacheEntry CreateTestEntryWithExpires(DateTimeOffset expires)
    {
        return new HttpCacheEntry
        {
            RequestUri = "https://api.example.com/users",
            Method = "GET",
            StatusCode = HttpStatusCode.OK,
            Content = "test content"u8.ToArray(),
            Headers = new Dictionary<string, string[]>(),
            ContentHeaders = new Dictionary<string, string[]>
            {
                ["Content-Type"] = new[] { "application/json; charset=utf-8" }
            },
            ETag = "\"abc123\"",
            Date = DateTimeOffset.UtcNow,
            StoredAt = DateTimeOffset.UtcNow,
            Expires = expires
        };
    }

    private static HttpCacheEntry CreateTestEntryWithLastModified(DateTimeOffset lastModified)
    {
        return new HttpCacheEntry
        {
            RequestUri = "https://api.example.com/users",
            Method = "GET",
            StatusCode = HttpStatusCode.OK,
            Content = "test content"u8.ToArray(),
            Headers = new Dictionary<string, string[]>(),
            ContentHeaders = new Dictionary<string, string[]>
            {
                ["Content-Type"] = new[] { "application/json; charset=utf-8" }
            },
            ETag = "\"abc123\"",
            Date = DateTimeOffset.UtcNow,
            StoredAt = DateTimeOffset.UtcNow,
            LastModified = lastModified,
            CacheControl = null,
            Expires = null
        };
    }

    private static HttpCacheEntry CreateTestEntryWithNoCacheDirectives()
    {
        return new HttpCacheEntry
        {
            RequestUri = "https://api.example.com/users",
            Method = "GET",
            StatusCode = HttpStatusCode.OK,
            Content = "test content"u8.ToArray(),
            Headers = new Dictionary<string, string[]>(),
            ContentHeaders = new Dictionary<string, string[]>
            {
                ["Content-Type"] = new[] { "application/json; charset=utf-8" }
            },
            ETag = "\"abc123\"",
            Date = DateTimeOffset.UtcNow,
            StoredAt = DateTimeOffset.UtcNow,
            CacheControl = null,
            Expires = null,
            LastModified = null
        };
    }

    private static bool ValidateTags(IEnumerable<string> tags)
    {
        var tagList = tags.ToList();

        // Should contain universal http-cache tag
        if (!tagList.Contains("http-cache"))
            return false;

        // Should contain method tag
        if (!tagList.Any(t => t.StartsWith("method:")))
            return false;

        // Should contain host tag
        if (!tagList.Any(t => t.StartsWith("host:")))
            return false;

        // Should contain content-type tag
        if (!tagList.Any(t => t.StartsWith("content-type:")))
            return false;

        // Should contain status tag
        if (!tagList.Any(t => t.StartsWith("status:")))
            return false;

        return true;
    }
}