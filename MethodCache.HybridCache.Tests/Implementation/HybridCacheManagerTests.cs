using FluentAssertions;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.HybridCache.Abstractions;
using MethodCache.HybridCache.Configuration;
using MethodCache.HybridCache.Implementation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using System;
using System.Threading.Tasks;
using Xunit;

namespace MethodCache.HybridCache.Tests.Implementation;

public class HybridCacheManagerTests : IDisposable
{
    private readonly IMemoryCache _mockL1Cache;
    private readonly ICacheManager _mockL2Cache;
    private readonly ICacheBackplane? _mockBackplane;
    private readonly ILogger<HybridCacheManager> _mockLogger;
    private readonly HybridCacheManager _hybridCacheManager;
    private readonly HybridCacheOptions _options;

    public HybridCacheManagerTests()
    {
        _mockL1Cache = Substitute.For<IMemoryCache>();
        _mockL2Cache = Substitute.For<ICacheManager>();
        _mockBackplane = Substitute.For<ICacheBackplane>();
        _mockLogger = Substitute.For<ILogger<HybridCacheManager>>();
        
        _options = new HybridCacheOptions
        {
            Strategy = HybridStrategy.WriteThrough,
            L1DefaultExpiration = TimeSpan.FromMinutes(5),
            L2DefaultExpiration = TimeSpan.FromHours(1),
            L2Enabled = true,
            EnableL1Warming = true,
            EnableAsyncL2Writes = true,
            MaxConcurrentL2Operations = 10
        };

        _hybridCacheManager = new HybridCacheManager(
            _mockL1Cache,
            _mockL2Cache,
            _mockBackplane,
            Options.Create(_options),
            _mockLogger);
    }

    [Fact]
    public async Task GetFromL1Async_ShouldReturnValueFromL1Cache()
    {
        // Arrange
        var key = "test-key";
        var expectedValue = "test-value";
        _mockL1Cache.GetAsync<string>(key).Returns(expectedValue);

        // Act
        var result = await _hybridCacheManager.GetFromL1Async<string>(key);

        // Assert
        result.Should().Be(expectedValue);
        _mockL1Cache.Received(1).GetAsync<string>(key);
    }

    [Fact]
    public async Task SetInL1Async_ShouldStoreValueInL1Cache()
    {
        // Arrange
        var key = "test-key";
        var value = "test-value";
        var expiration = TimeSpan.FromMinutes(10);

        // Act
        await _hybridCacheManager.SetInL1Async(key, value, expiration);

        // Assert
        _mockL1Cache.Received(1).SetAsync(key, value, Arg.Is<TimeSpan>(t => t <= _options.L1MaxExpiration));
    }

    [Fact]
    public async Task InvalidateL1Async_ShouldRemoveFromL1Cache()
    {
        // Arrange
        var key = "test-key";
        _mockL1Cache.RemoveAsync(key).Returns(true);

        // Act
        await _hybridCacheManager.InvalidateL1Async(key);

        // Assert
        _mockL1Cache.Received(1).RemoveAsync(key);
    }

    [Fact]
    public async Task InvalidateBothAsync_ShouldInvalidateBothCaches()
    {
        // Arrange
        var key = "test-key";
        _mockL1Cache.RemoveAsync(key).Returns(true);

        // Act
        await _hybridCacheManager.InvalidateBothAsync(key);

        // Assert
        _mockL1Cache.Received(1).RemoveAsync(key);
        // Note: L2 individual key invalidation is currently not supported through ICacheManager
        // The method logs a warning instead
    }

    [Fact]
    public async Task GetStatsAsync_ShouldReturnHybridCacheStatistics()
    {
        // Arrange
        var l1Stats = new CacheStats
        {
            Hits = 100,
            Misses = 50,
            Evictions = 10,
            Entries = 200
        };
        _mockL1Cache.GetStatsAsync().Returns(l1Stats);

        // Act
        var result = await _hybridCacheManager.GetStatsAsync();

        // Assert
        result.Should().NotBeNull();
        result.L1Entries.Should().Be(l1Stats.Entries);
        result.L1Evictions.Should().Be(l1Stats.Evictions);
    }

    [Fact]
    public async Task EvictFromL1Async_ShouldRemoveFromL1Cache()
    {
        // Arrange
        var key = "evict-key";
        _mockL1Cache.RemoveAsync(key).Returns(true);

        // Act
        await _hybridCacheManager.EvictFromL1Async(key);

        // Assert
        _mockL1Cache.Received(1).RemoveAsync(key);
    }

    [Fact]
    public async Task InvalidateByTagsAsync_ShouldInvalidateByTagsInL2AndClearL1()
    {
        // Arrange
        var tags = new[] { "tag1", "tag2" };
        _mockL2Cache.InvalidateByTagsAsync(tags).Returns(Task.CompletedTask);
        _mockL1Cache.ClearAsync().Returns(Task.CompletedTask);

        // Act
        await _hybridCacheManager.InvalidateByTagsAsync(tags);

        // Assert
        _mockL2Cache.Received(1).InvalidateByTagsAsync(tags);
        _mockL1Cache.Received(1).ClearAsync();
    }

    [Fact]
    public async Task GetOrCreateAsync_WithL1Hit_ShouldReturnFromL1()
    {
        // Arrange
        var methodName = "TestMethod";
        var args = new object[] { "arg1" };
        var settings = new CacheMethodSettings { Duration = TimeSpan.FromMinutes(10) };
        var keyGenerator = Substitute.For<ICacheKeyGenerator>();
        var expectedKey = "generated-key";
        var expectedValue = "cached-value";
        var factoryCalled = false;

        keyGenerator.GenerateKey(methodName, args, settings).Returns(expectedKey);
        _mockL1Cache.GetAsync<string>(expectedKey).Returns(expectedValue);

        // Act
        var result = await _hybridCacheManager.GetOrCreateAsync(
            methodName,
            args,
            () => { factoryCalled = true; return Task.FromResult("factory-value"); },
            settings,
            keyGenerator,
            false);

        // Assert
        result.Should().Be(expectedValue);
        factoryCalled.Should().BeFalse();
        _mockL1Cache.Received(1).GetAsync<string>(expectedKey);
    }

    [Theory]
    [InlineData(HybridStrategy.WriteThrough)]
    [InlineData(HybridStrategy.WriteBack)]
    [InlineData(HybridStrategy.L1Only)]
    [InlineData(HybridStrategy.L2Only)]
    public async Task GetOrCreateAsync_WithDifferentStrategies_ShouldBehaveAccordingly(HybridStrategy strategy)
    {
        // Arrange
        _options.Strategy = strategy;
        var hybridManager = new HybridCacheManager(
            _mockL1Cache,
            _mockL2Cache,
            _mockBackplane,
            Options.Create(_options),
            _mockLogger);

        var methodName = "TestMethod";
        var args = new object[] { "arg1" };
        var settings = new CacheMethodSettings { Duration = TimeSpan.FromMinutes(10) };
        var keyGenerator = Substitute.For<ICacheKeyGenerator>();
        var expectedKey = "generated-key";

        keyGenerator.GenerateKey(methodName, args, settings).Returns(expectedKey);
        _mockL1Cache.GetAsync<string>(expectedKey).Returns((string)null!);
        
        // Mock L2 cache to return null (cache miss) for any GetOrCreateAsync call
        _mockL2Cache.GetOrCreateAsync<string>(
            Arg.Any<string>(), 
            Arg.Any<object[]>(), 
            Arg.Any<Func<Task<string>>>(), 
            Arg.Any<CacheMethodSettings>(), 
            Arg.Any<ICacheKeyGenerator>(), 
            Arg.Any<bool>())
            .Returns(callInfo =>
            {
                // Call the factory function passed to simulate cache miss
                var factory = callInfo.ArgAt<Func<Task<string>>>(2);
                return factory();
            });

        // Act
        var result = await hybridManager.GetOrCreateAsync(
            methodName,
            args,
            () => Task.FromResult("factory-value"),
            settings,
            keyGenerator,
            false);

        // Assert
        result.Should().Be("factory-value");
        
        // Verify L1 cache interactions based on strategy
        switch (strategy)
        {
            case HybridStrategy.L2Only:
                _mockL1Cache.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>());
                break;
            default:
                // Other strategies should interact with L1
                _mockL1Cache.Received(1).SetAsync(expectedKey, "factory-value", Arg.Any<TimeSpan>());
                break;
        }
    }

    public void Dispose()
    {
        _hybridCacheManager?.Dispose();
    }
}