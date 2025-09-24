using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MethodCache.Infrastructure.Configuration;
using MethodCache.Infrastructure.Implementation;
using Xunit;

namespace MethodCache.Infrastructure.Tests;

public class MemoryStorageTests : IDisposable
{
    private readonly MemoryCache _memoryCache;
    private readonly MemoryStorage _storage;
    private readonly StorageOptions _options;

    public MemoryStorageTests()
    {
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        _options = new StorageOptions
        {
            EnableEfficientL1TagInvalidation = true,
            MaxTagMappings = 1000
        };
        _storage = new MemoryStorage(_memoryCache, Options.Create(_options), NullLogger<MemoryStorage>.Instance);
    }

    [Fact]
    public void Get_WhenKeyExists_ReturnsValue()
    {
        // Arrange
        const string key = "test-key";
        const string value = "test-value";
        _storage.Set(key, value, TimeSpan.FromMinutes(1));

        // Act
        var result = _storage.Get<string>(key);

        // Assert
        result.Should().Be(value);
    }

    [Fact]
    public void Get_WhenKeyDoesNotExist_ReturnsDefault()
    {
        // Arrange
        const string key = "non-existent-key";

        // Act
        var result = _storage.Get<string>(key);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WhenKeyExists_ReturnsValue()
    {
        // Arrange
        const string key = "test-key";
        const int value = 42;
        await _storage.SetAsync(key, value, TimeSpan.FromMinutes(1));

        // Act
        var result = await _storage.GetAsync<int>(key);

        // Assert
        result.Should().Be(value);
    }

    [Fact]
    public void Set_WithExpiration_StoresValue()
    {
        // Arrange
        const string key = "test-key";
        const string value = "test-value";
        var expiration = TimeSpan.FromMinutes(1);

        // Act
        _storage.Set(key, value, expiration);

        // Assert
        var result = _storage.Get<string>(key);
        result.Should().Be(value);
    }

    [Fact]
    public void Set_WithTags_StoresValueAndTracks()
    {
        // Arrange
        const string key = "test-key";
        const string value = "test-value";
        var tags = new[] { "tag1", "tag2" };

        // Act
        _storage.Set(key, value, TimeSpan.FromMinutes(1), tags);

        // Assert
        var result = _storage.Get<string>(key);
        result.Should().Be(value);

        var stats = _storage.GetStats();
        stats.TagMappingCount.Should().Be(2); // 2 tag mappings created
    }

    [Fact]
    public async Task SetAsync_WithTags_StoresValueAndTracks()
    {
        // Arrange
        const string key = "test-key";
        const string value = "test-value";
        var tags = new[] { "tag1", "tag2", "tag3" };

        // Act
        await _storage.SetAsync(key, value, TimeSpan.FromMinutes(1), tags);

        // Assert
        var result = await _storage.GetAsync<string>(key);
        result.Should().Be(value);

        var stats = _storage.GetStats();
        stats.TagMappingCount.Should().Be(3);
    }

    [Fact]
    public void Remove_WhenKeyExists_RemovesValue()
    {
        // Arrange
        const string key = "test-key";
        _storage.Set(key, "test-value", TimeSpan.FromMinutes(1));

        // Act
        _storage.Remove(key);

        // Assert
        var result = _storage.Get<string>(key);
        result.Should().BeNull();
    }

    [Fact]
    public async Task RemoveAsync_WhenKeyExists_RemovesValue()
    {
        // Arrange
        const string key = "test-key";
        await _storage.SetAsync(key, "test-value", TimeSpan.FromMinutes(1));

        // Act
        await _storage.RemoveAsync(key);

        // Assert
        var result = await _storage.GetAsync<string>(key);
        result.Should().BeNull();
    }

    [Fact]
    public void RemoveByTag_WithTrackedTags_RemovesAllMatchingKeys()
    {
        // Arrange
        const string tag = "shared-tag";
        _storage.Set("key1", "value1", TimeSpan.FromMinutes(1), new[] { tag, "other-tag" });
        _storage.Set("key2", "value2", TimeSpan.FromMinutes(1), new[] { tag });
        _storage.Set("key3", "value3", TimeSpan.FromMinutes(1), new[] { "different-tag" });

        // Act
        _storage.RemoveByTag(tag);

        // Assert
        _storage.Get<string>("key1").Should().BeNull();
        _storage.Get<string>("key2").Should().BeNull();
        _storage.Get<string>("key3").Should().Be("value3"); // Should remain
    }

    [Fact]
    public async Task RemoveByTagAsync_WithTrackedTags_RemovesAllMatchingKeys()
    {
        // Arrange
        const string tag = "async-tag";
        await _storage.SetAsync("async-key1", "value1", TimeSpan.FromMinutes(1), new[] { tag });
        await _storage.SetAsync("async-key2", "value2", TimeSpan.FromMinutes(1), new[] { tag });
        await _storage.SetAsync("async-key3", "value3", TimeSpan.FromMinutes(1), new[] { "other-tag" });

        // Act
        await _storage.RemoveByTagAsync(tag);

        // Assert
        (await _storage.GetAsync<string>("async-key1")).Should().BeNull();
        (await _storage.GetAsync<string>("async-key2")).Should().BeNull();
        (await _storage.GetAsync<string>("async-key3")).Should().Be("value3");
    }

    [Fact]
    public void RemoveByTag_WhenTagInvalidationDisabled_ClearsEntireCache()
    {
        // Arrange
        var optionsWithoutTagInvalidation = new StorageOptions
        {
            EnableEfficientL1TagInvalidation = false
        };
        var storageWithoutTags = new MemoryStorage(
            _memoryCache,
            Options.Create(optionsWithoutTagInvalidation),
            NullLogger<MemoryStorage>.Instance);

        storageWithoutTags.Set("key1", "value1", TimeSpan.FromMinutes(1));
        storageWithoutTags.Set("key2", "value2", TimeSpan.FromMinutes(1));

        // Act
        storageWithoutTags.RemoveByTag("any-tag");

        // Assert
        storageWithoutTags.Get<string>("key1").Should().BeNull();
        storageWithoutTags.Get<string>("key2").Should().BeNull();
    }

    [Fact]
    public void Exists_WhenKeyExists_ReturnsTrue()
    {
        // Arrange
        const string key = "test-key";
        _storage.Set(key, "test-value", TimeSpan.FromMinutes(1));

        // Act
        var exists = _storage.Exists(key);

        // Assert
        exists.Should().BeTrue();
    }

    [Fact]
    public void Exists_WhenKeyDoesNotExist_ReturnsFalse()
    {
        // Arrange
        const string key = "non-existent-key";

        // Act
        var exists = _storage.Exists(key);

        // Assert
        exists.Should().BeFalse();
    }

    [Fact]
    public void GetStats_AfterOperations_ReturnsAccurateStatistics()
    {
        // Arrange
        _storage.Set("key1", "value1", TimeSpan.FromMinutes(1), new[] { "tag1" });
        _storage.Set("key2", "value2", TimeSpan.FromMinutes(1), new[] { "tag2" });

        // Generate some hits and misses
        _storage.Get<string>("key1"); // Hit
        _storage.Get<string>("key1"); // Hit
        _storage.Get<string>("non-existent"); // Miss

        // Act
        var stats = _storage.GetStats();

        // Assert
        stats.Hits.Should().Be(2);
        stats.Misses.Should().Be(1);
        stats.HitRatio.Should().BeApproximately(2.0 / 3.0, 0.01);
        stats.TagMappingCount.Should().Be(2);
        stats.EstimatedMemoryUsage.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        // Arrange
        _storage.Set("key1", "value1", TimeSpan.FromMinutes(1));
        _storage.Set("key2", "value2", TimeSpan.FromMinutes(1));

        // Act
        _storage.Clear();

        // Assert
        _storage.Get<string>("key1").Should().BeNull();
        _storage.Get<string>("key2").Should().BeNull();

        var stats = _storage.GetStats();
        stats.TagMappingCount.Should().Be(0);
    }

    [Fact]
    public void Set_WhenTagMappingLimitReached_DoesNotTrackAdditionalTags()
    {
        // Arrange
        var limitedOptions = new StorageOptions
        {
            EnableEfficientL1TagInvalidation = true,
            MaxTagMappings = 2 // Very low limit for testing
        };
        var limitedStorage = new MemoryStorage(
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(limitedOptions),
            NullLogger<MemoryStorage>.Instance);

        // Act
        limitedStorage.Set("key1", "value1", TimeSpan.FromMinutes(1), new[] { "tag1" });
        limitedStorage.Set("key2", "value2", TimeSpan.FromMinutes(1), new[] { "tag2" });
        limitedStorage.Set("key3", "value3", TimeSpan.FromMinutes(1), new[] { "tag3" }); // Should not be tracked

        // Assert
        var stats = limitedStorage.GetStats();
        stats.TagMappingCount.Should().BeLessOrEqualTo(2);

        // The value should still be stored, just not tag-tracked
        limitedStorage.Get<string>("key3").Should().Be("value3");
    }

    [Theory]
    [InlineData("string value")]
    [InlineData(42)]
    [InlineData(3.14159)]
    [InlineData(true)]
    public void Storage_SupportsMultipleDataTypes<T>(T value)
    {
        // Arrange
        const string key = "typed-key";

        // Act
        _storage.Set(key, value, TimeSpan.FromMinutes(1));
        var result = _storage.Get<T>(key);

        // Assert
        result.Should().Be(value);
    }

    public void Dispose()
    {
        _memoryCache?.Dispose();
    }
}