using FluentAssertions;
using MethodCache.Providers.Redis.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using System;
using System.Threading.Tasks;
using Xunit;

namespace MethodCache.Providers.Redis.Tests.Hybrid;

public class MemoryL1CacheTests : IDisposable
{
    private readonly MemoryL1Cache _cache;
    private readonly ILogger<MemoryL1Cache> _mockLogger;
    private readonly HybridCacheOptions _options;

    public MemoryL1CacheTests()
    {
        _mockLogger = Substitute.For<ILogger<MemoryL1Cache>>();
        _options = new HybridCacheOptions
        {
            L1MaxItems = 100,
            L1DefaultExpiration = TimeSpan.FromMinutes(5),
            L1EvictionPolicy = L1EvictionPolicy.LRU,
            L1SlidingExpiration = true
        };
        
        _cache = new MemoryL1Cache(Options.Create(_options), _mockLogger);
    }

    [Fact]
    public async Task SetAsync_ShouldStoreValue_WhenValidKeyAndValue()
    {
        // Arrange
        var key = "test-key";
        var value = "test-value";
        var expiration = TimeSpan.FromMinutes(1);

        // Act
        await _cache.SetAsync(key, value, expiration);

        // Assert
        var result = await _cache.GetAsync<string>(key);
        result.Should().Be(value);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenKeyDoesNotExist()
    {
        // Arrange
        var key = "non-existent-key";

        // Act
        var result = await _cache.GetAsync<string>(key);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenKeyIsExpired()
    {
        // Arrange
        var key = "expired-key";
        var value = "expired-value";
        var expiration = TimeSpan.FromMilliseconds(50);

        await _cache.SetAsync(key, value, expiration);
        
        // Act - Wait for expiration
        await Task.Delay(100);
        var result = await _cache.GetAsync<string>(key);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task RemoveAsync_ShouldDeleteValue_WhenKeyExists()
    {
        // Arrange
        var key = "remove-key";
        var value = "remove-value";

        await _cache.SetAsync(key, value, TimeSpan.FromMinutes(1));

        // Act
        var removed = await _cache.RemoveAsync(key);

        // Assert
        removed.Should().BeTrue();
        var result = await _cache.GetAsync<string>(key);
        result.Should().BeNull();
    }

    [Fact]
    public async Task ClearAsync_ShouldRemoveAllKeys()
    {
        // Arrange
        await _cache.SetAsync("key1", "value1", TimeSpan.FromMinutes(1));
        await _cache.SetAsync("key2", "value2", TimeSpan.FromMinutes(1));

        // Act
        await _cache.ClearAsync();

        // Assert
        var result1 = await _cache.GetAsync<string>("key1");
        var result2 = await _cache.GetAsync<string>("key2");
        result1.Should().BeNull();
        result2.Should().BeNull();
    }

    [Fact]
    public async Task ExistsAsync_ShouldReturnCorrectStatus()
    {
        // Arrange
        var key = "exists-key";
        var value = "exists-value";

        // Act & Assert - Key doesn't exist initially
        var existsInitially = await _cache.ExistsAsync(key);
        existsInitially.Should().BeFalse();

        // Set the key
        await _cache.SetAsync(key, value, TimeSpan.FromMinutes(1));

        // Act & Assert - Key exists after setting
        var existsAfterSet = await _cache.ExistsAsync(key);
        existsAfterSet.Should().BeTrue();

        // Remove the key
        await _cache.RemoveAsync(key);

        // Act & Assert - Key doesn't exist after removal
        var existsAfterRemove = await _cache.ExistsAsync(key);
        existsAfterRemove.Should().BeFalse();
    }

    [Fact]
    public async Task GetCountAsync_ShouldReturnCorrectCount()
    {
        // Arrange
        var initialCount = await _cache.GetCountAsync();
        
        // Act
        await _cache.SetAsync("key1", "value1", TimeSpan.FromMinutes(1));
        await _cache.SetAsync("key2", "value2", TimeSpan.FromMinutes(1));
        
        var countAfterSet = await _cache.GetCountAsync();
        
        await _cache.RemoveAsync("key1");
        var countAfterRemove = await _cache.GetCountAsync();

        // Assert
        initialCount.Should().Be(0);
        countAfterSet.Should().Be(2);
        countAfterRemove.Should().Be(1);
    }

    [Fact]
    public async Task GetStatsAsync_ShouldReturnAccurateStatistics()
    {
        // Arrange & Act
        await _cache.SetAsync("key1", "value1", TimeSpan.FromMinutes(1));
        
        // Hit
        var value1 = await _cache.GetAsync<string>("key1");
        
        // Miss
        var value2 = await _cache.GetAsync<string>("nonexistent");
        
        var stats = await _cache.GetStatsAsync();

        // Assert
        stats.Should().NotBeNull();
        stats.Hits.Should().BeGreaterThan(0);
        stats.Misses.Should().BeGreaterThan(0);
        stats.Entries.Should().Be(1);
        stats.HitRatio.Should().BeGreaterThan(0).And.BeLessOrEqualTo(1);
    }

    [Fact]
    public async Task EvictExpiredAsync_ShouldRemoveExpiredEntries()
    {
        // Arrange
        await _cache.SetAsync("key1", "value1", TimeSpan.FromMilliseconds(50));
        await _cache.SetAsync("key2", "value2", TimeSpan.FromMinutes(10));

        // Wait for first key to expire
        await Task.Delay(100);

        // Act
        await _cache.EvictExpiredAsync();

        // Assert
        var value1 = await _cache.GetAsync<string>("key1");
        var value2 = await _cache.GetAsync<string>("key2");
        
        value1.Should().BeNull();
        value2.Should().Be("value2");
    }

    [Fact]
    public async Task TryEvictLRUAsync_ShouldEvictLeastRecentlyUsedItem()
    {
        // Arrange - Fill cache to capacity
        for (int i = 0; i < _options.L1MaxItems; i++)
        {
            await _cache.SetAsync($"key{i}", $"value{i}", TimeSpan.FromMinutes(10));
        }

        // Access some keys to make them more recently used
        await _cache.GetAsync<string>("key10");
        await _cache.GetAsync<string>("key20");
        
        // Add one more item to trigger eviction
        await _cache.SetAsync("overflow-key", "overflow-value", TimeSpan.FromMinutes(10));

        // Act
        var evicted = await _cache.TryEvictLRUAsync();

        // Assert
        evicted.Should().BeTrue();
        
        // The recently accessed keys should still exist
        var value10 = await _cache.GetAsync<string>("key10");
        var value20 = await _cache.GetAsync<string>("key20");
        
        value10.Should().Be("value10");
        value20.Should().Be("value20");
    }

    [Fact]
    public async Task GetAsync_WithTypeMismatch_ShouldReturnNull()
    {
        // Arrange
        var key = "type-mismatch-key";
        await _cache.SetAsync(key, "string-value", TimeSpan.FromMinutes(1));

        // Act - Try to get as different type
        var result = await _cache.GetAsync<int>(key);

        // Assert
        result.Should().Be(0); // Default value for int
    }

    [Fact]
    public async Task GetKeysAsync_ShouldReturnMatchingKeys()
    {
        // Arrange
        await _cache.SetAsync("prefix:key1", "value1", TimeSpan.FromMinutes(1));
        await _cache.SetAsync("prefix:key2", "value2", TimeSpan.FromMinutes(1));
        await _cache.SetAsync("other:key3", "value3", TimeSpan.FromMinutes(1));

        // Act
        var allKeys = await _cache.GetKeysAsync();
        var prefixedKeys = await _cache.GetKeysAsync("prefix:*");

        // Assert
        allKeys.Should().HaveCount(3);
        prefixedKeys.Should().HaveCount(2);
        prefixedKeys.Should().Contain("prefix:key1").And.Contain("prefix:key2");
    }

    public void Dispose()
    {
        _cache?.Dispose();
    }
}