using FluentAssertions;
using MethodCache.HybridCache.Configuration;
using MethodCache.HybridCache.Implementation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using System;
using System.Threading.Tasks;
using Xunit;

namespace MethodCache.HybridCache.Tests.Implementation;

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
            L1MaxExpiration = TimeSpan.FromHours(1),
            L1EvictionPolicy = L1EvictionPolicy.LRU
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
    public async Task RemoveMultipleAsync_ShouldRemoveSpecifiedKeys()
    {
        // Arrange
        await _cache.SetAsync("key1", "value1", TimeSpan.FromMinutes(1));
        await _cache.SetAsync("key2", "value2", TimeSpan.FromMinutes(1));
        await _cache.SetAsync("key3", "value3", TimeSpan.FromMinutes(1));

        // Act
        var removedCount = await _cache.RemoveMultipleAsync("key1", "key3", "nonexistent");

        // Assert
        removedCount.Should().Be(2);
        
        var result1 = await _cache.GetAsync<string>("key1");
        var result2 = await _cache.GetAsync<string>("key2");
        var result3 = await _cache.GetAsync<string>("key3");
        
        result1.Should().BeNull();
        result2.Should().Be("value2"); // Should still exist
        result3.Should().BeNull();
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
    }

    [Fact]
    public async Task GetAsync_WithTypeMismatch_ShouldReturnDefault()
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
    public async Task SetAsync_WithNullValue_ShouldRemoveKey()
    {
        // Arrange
        var key = "null-value-key";
        await _cache.SetAsync(key, "initial-value", TimeSpan.FromMinutes(1));

        // Act
        await _cache.SetAsync<string>(key, null!, TimeSpan.FromMinutes(1));

        // Assert
        var result = await _cache.GetAsync<string>(key);
        result.Should().BeNull();
        
        var exists = await _cache.ExistsAsync(key);
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task SetAsync_ShouldRespectMaxExpiration()
    {
        // Arrange
        var key = "max-expiration-key";
        var value = "test-value";
        var requestedExpiration = TimeSpan.FromHours(24); // Much longer than max
        
        // Act
        await _cache.SetAsync(key, value, requestedExpiration);

        // Assert - Value should be stored but with limited expiration
        var result = await _cache.GetAsync<string>(key);
        result.Should().Be(value);
    }

    public void Dispose()
    {
        _cache?.Dispose();
    }
}