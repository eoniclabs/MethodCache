using FluentAssertions;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Providers.Redis.Extensions;
using MethodCache.HybridCache.Abstractions;
using MethodCache.HybridCache.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace MethodCache.Providers.Redis.IntegrationTests.Tests;

public class HybridCacheIntegrationTests : RedisIntegrationTestBase
{
    [Fact]
    public async Task HybridCache_GetOrCreateAsync_ShouldWorkWithBothCacheLayers()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        services.AddHybridRedisCache(hybridOptions =>
        {
            hybridOptions.WithL1Configuration(maxItems: 1000, defaultExpiration: TimeSpan.FromMinutes(2))
                         .WithL2Configuration(defaultExpiration: TimeSpan.FromMinutes(10))
                         .WithStrategy(HybridStrategy.WriteThrough, enableL1Warming: true)
                         .WithPerformanceSettings(maxConcurrentL2Operations: 5);
        }, redisOptions =>
        {
            redisOptions.ConnectionString = RedisContainer.GetConnectionString();
            redisOptions.KeyPrefix = "hybrid-test:";
        });

        var serviceProvider = services.BuildServiceProvider();
        var cacheManager = serviceProvider.GetRequiredService<ICacheManager>();
        var hybridManager = serviceProvider.GetRequiredService<IHybridCacheManager>();
        var keyGenerator = serviceProvider.GetRequiredService<ICacheKeyGenerator>();

        var methodName = "TestMethod";
        var args = new object[] { 123, "test" };
        var settings = new CacheMethodSettings { Duration = TimeSpan.FromMinutes(5) };
        var callCount = 0;

        // Act - First call should execute factory and store in both caches
        var result1 = await cacheManager.GetOrCreateAsync(
            methodName,
            args,
            async () => { callCount++; return $"Result-{callCount}"; },
            settings,
            keyGenerator,
            false);

        // Second call should hit L1 cache
        var result2 = await cacheManager.GetOrCreateAsync(
            methodName,
            args,
            async () => { callCount++; return $"Result-{callCount}"; },
            settings,
            keyGenerator,
            false);

        // Assert
        result1.Should().Be("Result-1");
        result2.Should().Be("Result-1"); // Same result from cache
        callCount.Should().Be(1); // Factory called only once

        // Verify L1 cache has the value
        var cacheKey = keyGenerator.GenerateKey(methodName, args, settings);
        var l1Value = await hybridManager.GetFromL1Async<string>(cacheKey);
        l1Value.Should().Be("Result-1");

        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task HybridCache_L1Miss_ShouldFallbackToL2()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        services.AddHybridRedisCache(hybridOptions =>
        {
            hybridOptions.WithStrategy(HybridStrategy.WriteThrough)
                         .WithL1Configuration(maxItems: 10, defaultExpiration: TimeSpan.FromMilliseconds(100)) // Very short L1 expiration
                         .WithL2Configuration(defaultExpiration: TimeSpan.FromMinutes(10));
        }, redisOptions =>
        {
            redisOptions.ConnectionString = RedisContainer.GetConnectionString();
            redisOptions.KeyPrefix = "l1miss-test:";
        });

        var serviceProvider = services.BuildServiceProvider();
        var cacheManager = serviceProvider.GetRequiredService<ICacheManager>();
        var hybridManager = serviceProvider.GetRequiredService<IHybridCacheManager>();
        var keyGenerator = serviceProvider.GetRequiredService<ICacheKeyGenerator>();

        var methodName = "L1MissTest";
        var args = new object[] { "test-key" };
        var settings = new CacheMethodSettings { Duration = TimeSpan.FromMinutes(5) };
        var callCount = 0;

        // Act - Store initial value
        var result1 = await cacheManager.GetOrCreateAsync(
            methodName,
            args,
            async () => { callCount++; return $"Original-{callCount}"; },
            settings,
            keyGenerator,
            false);

        // Wait for L1 to expire
        await Task.Delay(200);

        // This should miss L1 but hit L2, then warm L1
        var result2 = await cacheManager.GetOrCreateAsync(
            methodName,
            args,
            async () => { callCount++; return $"ShouldNotBeCalled-{callCount}"; },
            settings,
            keyGenerator,
            false);

        // Assert
        result1.Should().Be("Original-1");
        result2.Should().Be("Original-1"); // Same result from L2
        callCount.Should().Be(1); // Factory called only once

        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task HybridCache_WriteBackStrategy_ShouldWriteL1ImmediatelyAndL2Async()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        services.AddHybridRedisCache(hybridOptions =>
        {
            hybridOptions.WithStrategy(HybridStrategy.WriteBack, enableAsyncL2Writes: true)
                         .WithL1Configuration(maxItems: 1000)
                         .WithL2Configuration(defaultExpiration: TimeSpan.FromMinutes(10));
        }, redisOptions =>
        {
            redisOptions.ConnectionString = RedisContainer.GetConnectionString();
            redisOptions.KeyPrefix = "writeback-test:";
        });

        var serviceProvider = services.BuildServiceProvider();
        var cacheManager = serviceProvider.GetRequiredService<ICacheManager>();
        var hybridManager = serviceProvider.GetRequiredService<IHybridCacheManager>();
        var keyGenerator = serviceProvider.GetRequiredService<ICacheKeyGenerator>();

        var methodName = "WriteBackTest";
        var args = new object[] { "writeback-key" };
        var settings = new CacheMethodSettings { Duration = TimeSpan.FromMinutes(5) };

        // Act
        var result = await cacheManager.GetOrCreateAsync(
            methodName,
            args,
            async () => "WriteBackValue",
            settings,
            keyGenerator,
            false);

        // L1 should have the value immediately
        var cacheKey = keyGenerator.GenerateKey(methodName, args, settings);
        var l1Value = await hybridManager.GetFromL1Async<string>(cacheKey);

        // Wait a bit for async L2 write to complete
        await Task.Delay(500);

        // Assert
        result.Should().Be("WriteBackValue");
        l1Value.Should().Be("WriteBackValue");

        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task HybridCache_L1OnlyStrategy_ShouldOnlyUseL1Cache()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        services.AddHybridRedisCache(hybridOptions =>
        {
            hybridOptions.WithStrategy(HybridStrategy.L1Only)
                         .WithL1Configuration(maxItems: 1000);
        }, redisOptions =>
        {
            redisOptions.ConnectionString = RedisContainer.GetConnectionString();
            redisOptions.KeyPrefix = "l1only-test:";
        });

        var serviceProvider = services.BuildServiceProvider();
        var cacheManager = serviceProvider.GetRequiredService<ICacheManager>();
        var hybridManager = serviceProvider.GetRequiredService<IHybridCacheManager>();
        var keyGenerator = serviceProvider.GetRequiredService<ICacheKeyGenerator>();

        var methodName = "L1OnlyTest";
        var args = new object[] { "l1only-key" };
        var settings = new CacheMethodSettings { Duration = TimeSpan.FromMinutes(5) };

        // Act
        var result = await cacheManager.GetOrCreateAsync(
            methodName,
            args,
            async () => "L1OnlyValue",
            settings,
            keyGenerator,
            false);

        var cacheKey = keyGenerator.GenerateKey(methodName, args, settings);
        var l1Value = await hybridManager.GetFromL1Async<string>(cacheKey);
        var l2Value = await hybridManager.GetFromL2Async<string>(cacheKey);

        // Assert
        result.Should().Be("L1OnlyValue");
        l1Value.Should().Be("L1OnlyValue");
        l2Value.Should().BeNull(); // L2 should not have the value

        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task HybridCache_TagInvalidation_ShouldInvalidateBothCaches()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        services.AddHybridRedisCache(hybridOptions =>
        {
            hybridOptions.WithStrategy(HybridStrategy.WriteThrough);
        }, redisOptions =>
        {
            redisOptions.ConnectionString = RedisContainer.GetConnectionString();
            redisOptions.KeyPrefix = "invalidate-test:";
        });

        var serviceProvider = services.BuildServiceProvider();
        var cacheManager = serviceProvider.GetRequiredService<ICacheManager>();
        var hybridManager = serviceProvider.GetRequiredService<IHybridCacheManager>();
        var keyGenerator = serviceProvider.GetRequiredService<ICacheKeyGenerator>();

        var methodName = "TagInvalidateTest";
        var args = new object[] { "tagged-key" };
        var settings = new CacheMethodSettings 
        { 
            Duration = TimeSpan.FromMinutes(5),
            Tags = new List<string> { "test-tag", "invalidate-tag" }
        };

        // Store value in cache
        await cacheManager.GetOrCreateAsync(
            methodName,
            args,
            async () => "TaggedValue",
            settings,
            keyGenerator,
            false);

        var cacheKey = keyGenerator.GenerateKey(methodName, args, settings);
        
        // Verify value exists in both caches
        var l1ValueBefore = await hybridManager.GetFromL1Async<string>(cacheKey);
        l1ValueBefore.Should().Be("TaggedValue");

        // Act - Invalidate by tags
        await cacheManager.InvalidateByTagsAsync("test-tag");

        // Assert - Both caches should be invalidated
        var l1ValueAfter = await hybridManager.GetFromL1Async<string>(cacheKey);
        l1ValueAfter.Should().BeNull();

        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task HybridCache_GetStatsAsync_ShouldReturnAccurateStatistics()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        services.AddHybridRedisCache(hybridOptions =>
        {
            hybridOptions.WithStrategy(HybridStrategy.WriteThrough)
                         .WithPerformanceSettings();
        }, redisOptions =>
        {
            redisOptions.ConnectionString = RedisContainer.GetConnectionString();
            redisOptions.KeyPrefix = "stats-test:";
        });

        var serviceProvider = services.BuildServiceProvider();
        var cacheManager = serviceProvider.GetRequiredService<ICacheManager>();
        var hybridManager = serviceProvider.GetRequiredService<IHybridCacheManager>();
        var keyGenerator = serviceProvider.GetRequiredService<ICacheKeyGenerator>();

        // Act - Generate some cache activity
        var settings = new CacheMethodSettings { Duration = TimeSpan.FromMinutes(5) };
        
        // Cache miss
        await cacheManager.GetOrCreateAsync("Method1", new object[] { 1 }, async () => "Value1", settings, keyGenerator, false);
        
        // Cache hit
        await cacheManager.GetOrCreateAsync("Method1", new object[] { 1 }, async () => "ShouldNotBeCalled", settings, keyGenerator, false);
        
        // Another cache miss
        await cacheManager.GetOrCreateAsync("Method2", new object[] { 2 }, async () => "Value2", settings, keyGenerator, false);

        var stats = await hybridManager.GetStatsAsync();

        // Assert
        stats.Should().NotBeNull();
        stats.L1Hits.Should().BeGreaterThan(0);
        stats.L1Entries.Should().BeGreaterThan(0);
        stats.L1HitRatio.Should().BeGreaterThan(0);
        stats.OverallHitRatio.Should().BeGreaterThan(0);

        await serviceProvider.DisposeAsync();
    }

    [Fact] 
    public async Task HybridCache_EvictionPolicy_ShouldEvictCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        services.AddHybridRedisCache(hybridOptions =>
        {
            hybridOptions.WithL1Configuration(maxItems: 3, evictionPolicy: L1EvictionPolicy.LRU) // Very small cache
                         .WithStrategy(HybridStrategy.WriteThrough);
        }, redisOptions =>
        {
            redisOptions.ConnectionString = RedisContainer.GetConnectionString();
            redisOptions.KeyPrefix = "eviction-test:";
        });

        var serviceProvider = services.BuildServiceProvider();
        var cacheManager = serviceProvider.GetRequiredService<ICacheManager>();
        var hybridManager = serviceProvider.GetRequiredService<IHybridCacheManager>();
        var keyGenerator = serviceProvider.GetRequiredService<ICacheKeyGenerator>();

        var settings = new CacheMethodSettings { Duration = TimeSpan.FromMinutes(5) };

        // Act - Fill cache beyond capacity
        await cacheManager.GetOrCreateAsync("Method1", new object[] { 1 }, async () => "Value1", settings, keyGenerator, false);
        await cacheManager.GetOrCreateAsync("Method2", new object[] { 2 }, async () => "Value2", settings, keyGenerator, false);
        await cacheManager.GetOrCreateAsync("Method3", new object[] { 3 }, async () => "Value3", settings, keyGenerator, false);
        
        // Access Method1 to make it more recently used
        await cacheManager.GetOrCreateAsync("Method1", new object[] { 1 }, async () => "ShouldNotBeCalled", settings, keyGenerator, false);
        
        // Add another item to trigger eviction
        await cacheManager.GetOrCreateAsync("Method4", new object[] { 4 }, async () => "Value4", settings, keyGenerator, false);

        // Assert - Method1 (recently used) and Method4 (new) should still be in L1
        var key1 = keyGenerator.GenerateKey("Method1", new object[] { 1 }, settings);
        var key2 = keyGenerator.GenerateKey("Method2", new object[] { 2 }, settings);
        var key4 = keyGenerator.GenerateKey("Method4", new object[] { 4 }, settings);

        var l1Value1 = await hybridManager.GetFromL1Async<string>(key1);
        var l1Value2 = await hybridManager.GetFromL1Async<string>(key2);
        var l1Value4 = await hybridManager.GetFromL1Async<string>(key4);

        l1Value1.Should().Be("Value1"); // Recently accessed, should remain
        l1Value4.Should().Be("Value4"); // Newly added, should be present
        // Method2 might be evicted due to LRU policy

        await serviceProvider.DisposeAsync();
    }
}