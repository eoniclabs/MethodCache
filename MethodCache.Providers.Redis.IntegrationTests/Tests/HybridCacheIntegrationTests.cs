using FluentAssertions;
using MethodCache.Core;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Configuration;
using MethodCache.Core.Runtime;
using MethodCache.Providers.Redis.Infrastructure;
using MethodCache.Infrastructure.Extensions;
using MethodCache.Core.Storage;
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
        
        // Add Redis infrastructure
        services.AddRedisHybridInfrastructureForTests(
            redisOptions =>
            {
                redisOptions.ConnectionString = RedisConnectionString;
                redisOptions.KeyPrefix = CreateKeyPrefix("hybrid-test");
                redisOptions.DefaultExpiration = TimeSpan.FromMinutes(10);
            },
            storageOptions =>
            {
                storageOptions.L2Enabled = true;
            });
        services.Configure<MethodCache.Core.Storage.HybridCacheOptions>(hybridOptions =>
        {
            hybridOptions.Strategy = MethodCache.Core.Storage.HybridStrategy.WriteThrough;
            hybridOptions.L2Enabled = true;
        });

        var serviceProvider = services.BuildServiceProvider();
        await StartHostedServicesAsync(serviceProvider);
        var cacheManager = serviceProvider.GetRequiredService<ICacheManager>();
        var hybridManager = serviceProvider.GetRequiredService<MethodCache.Core.Storage.IHybridCacheManager>();
        var keyGenerator = serviceProvider.GetRequiredService<ICacheKeyGenerator>();

        var methodName = "TestMethod";
        var args = new object[] { 123, "test" };
        var settings = CacheRuntimePolicy.FromPolicy("test", CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(5) }, CachePolicyFields.Duration);
        var callCount = 0;

        // Act - First call should execute factory and store in both caches
        var result1 = await cacheManager.GetOrCreateAsync(
            methodName,
            args,
            () => { callCount++; return Task.FromResult($"Result-{callCount}"); },
            settings,
            keyGenerator);

        // Second call should hit L1 cache
        var result2 = await cacheManager.GetOrCreateAsync(
            methodName,
            args,
            () => { callCount++; return Task.FromResult($"Result-{callCount}"); },
            settings,
            keyGenerator);

        // Assert
        result1.Should().Be("Result-1");
        result2.Should().Be("Result-1"); // Same result from cache
        callCount.Should().Be(1); // Factory called only once

        // Verify L1 cache has the value
        var cacheKey = keyGenerator.GenerateKey(methodName, args, settings);
        var l1Value = await hybridManager.GetFromL1Async<string>(cacheKey);
        l1Value.Should().Be("Result-1");

        await StopHostedServicesAsync(serviceProvider);
        await DisposeServiceProviderAsync(serviceProvider);
    }

    [Fact]
    public async Task HybridCache_L1Miss_ShouldFallbackToL2()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        // Add Redis infrastructure
        services.AddRedisHybridInfrastructureForTests(
            redisOptions =>
            {
                redisOptions.ConnectionString = RedisConnectionString;
                redisOptions.KeyPrefix = CreateKeyPrefix("l1miss-test");
                redisOptions.DefaultExpiration = TimeSpan.FromMinutes(10);
            },
            storageOptions =>
            {
                storageOptions.L2Enabled = true;
                storageOptions.L1DefaultExpiration = TimeSpan.FromMilliseconds(100);
            });
        services.Configure<MethodCache.Core.Storage.HybridCacheOptions>(hybridOptions =>
        {
            hybridOptions.Strategy = MethodCache.Core.Storage.HybridStrategy.WriteThrough;
            hybridOptions.L2Enabled = true;
            hybridOptions.L1MaxItems = 10;
            hybridOptions.L1DefaultExpiration = TimeSpan.FromMilliseconds(100); // Very short L1 expiration
        });

        var serviceProvider = services.BuildServiceProvider();
        await StartHostedServicesAsync(serviceProvider);
        var cacheManager = serviceProvider.GetRequiredService<ICacheManager>();
        var hybridManager = serviceProvider.GetRequiredService<MethodCache.Core.Storage.IHybridCacheManager>();
        var keyGenerator = serviceProvider.GetRequiredService<ICacheKeyGenerator>();

        var methodName = "L1MissTest";
        var args = new object[] { "test-key" };
        var settings = CacheRuntimePolicy.FromPolicy("test", CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(5) }, CachePolicyFields.Duration);
        var callCount = 0;

        // Act - Store initial value
        var result1 = await cacheManager.GetOrCreateAsync(
            methodName,
            args,
            () => { callCount++; return Task.FromResult($"Original-{callCount}"); },
            settings,
            keyGenerator);

        // Wait for L1 to expire
        await Task.Delay(200);

        // This should miss L1 but hit L2, then warm L1
        var result2 = await cacheManager.GetOrCreateAsync(
            methodName,
            args,
            () => { callCount++; return Task.FromResult($"ShouldNotBeCalled-{callCount}"); },
            settings,
            keyGenerator);

        // Assert
        result1.Should().Be("Original-1");
        result2.Should().Be("Original-1"); // Same result from L2
        callCount.Should().Be(1); // Factory called only once

        await StopHostedServicesAsync(serviceProvider);
        await DisposeServiceProviderAsync(serviceProvider);
    }

    [Fact]
    public async Task HybridCache_WriteBackStrategy_ShouldWriteL1ImmediatelyAndL2Async()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        // Add Redis infrastructure
        services.AddRedisHybridInfrastructureForTests(
            redisOptions =>
            {
                redisOptions.ConnectionString = RedisConnectionString;
                redisOptions.KeyPrefix = CreateKeyPrefix("writeback-test");
                redisOptions.DefaultExpiration = TimeSpan.FromMinutes(10);
            },
            storageOptions =>
            {
                storageOptions.L2Enabled = true;
            });
        services.Configure<MethodCache.Core.Storage.HybridCacheOptions>(hybridOptions =>
        {
            hybridOptions.Strategy = MethodCache.Core.Storage.HybridStrategy.WriteBack;
            hybridOptions.L2Enabled = true;
            hybridOptions.EnableAsyncL2Writes = true;
            hybridOptions.L1MaxItems = 1000;
        });

        var serviceProvider = services.BuildServiceProvider();
        await StartHostedServicesAsync(serviceProvider);
        var cacheManager = serviceProvider.GetRequiredService<ICacheManager>();
        var hybridManager = serviceProvider.GetRequiredService<MethodCache.Core.Storage.IHybridCacheManager>();
        var keyGenerator = serviceProvider.GetRequiredService<ICacheKeyGenerator>();

        var methodName = "WriteBackTest";
        var args = new object[] { "writeback-key" };
        var settings = CacheRuntimePolicy.FromPolicy("test", CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(5) }, CachePolicyFields.Duration);

        // Act
        var result = await cacheManager.GetOrCreateAsync(
            methodName,
            args,
            () => Task.FromResult("WriteBackValue"),
            settings,
            keyGenerator);

        // L1 should have the value immediately
        var cacheKey = keyGenerator.GenerateKey(methodName, args, settings);
        var l1Value = await hybridManager.GetFromL1Async<string>(cacheKey);

        // Wait a bit for async L2 write to complete
        await Task.Delay(500);

        // Assert
        result.Should().Be("WriteBackValue");
        l1Value.Should().Be("WriteBackValue");

        await StopHostedServicesAsync(serviceProvider);
        await DisposeServiceProviderAsync(serviceProvider);
    }

    [Fact]
    public async Task HybridCache_L1OnlyStrategy_ShouldOnlyUseL1Cache()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        // Add Redis infrastructure
        services.AddRedisHybridInfrastructureForTests(
            redisOptions =>
            {
                redisOptions.ConnectionString = RedisConnectionString;
                redisOptions.KeyPrefix = CreateKeyPrefix("l1only-test");
            },
            storageOptions =>
            {
                storageOptions.L2Enabled = false;
            });
        services.Configure<MethodCache.Core.Storage.HybridCacheOptions>(hybridOptions =>
        {
            hybridOptions.Strategy = MethodCache.Core.Storage.HybridStrategy.L1Only;
            hybridOptions.L2Enabled = false;
            hybridOptions.L1MaxItems = 1000;
        });

        var serviceProvider = services.BuildServiceProvider();
        await StartHostedServicesAsync(serviceProvider);
        var cacheManager = serviceProvider.GetRequiredService<ICacheManager>();
        var hybridManager = serviceProvider.GetRequiredService<MethodCache.Core.Storage.IHybridCacheManager>();
        var keyGenerator = serviceProvider.GetRequiredService<ICacheKeyGenerator>();

        var methodName = "L1OnlyTest";
        var args = new object[] { "l1only-key" };
        var settings = CacheRuntimePolicy.FromPolicy("test", CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(5) }, CachePolicyFields.Duration);

        // Act
        var result = await cacheManager.GetOrCreateAsync(
            methodName,
            args,
            () => Task.FromResult("L1OnlyValue"),
            settings,
            keyGenerator);

        var cacheKey = keyGenerator.GenerateKey(methodName, args, settings);
        var l1Value = await hybridManager.GetFromL1Async<string>(cacheKey);
        var l2Value = await hybridManager.GetFromL2Async<string>(cacheKey);

        // Assert
        result.Should().Be("L1OnlyValue");
        l1Value.Should().Be("L1OnlyValue");
        l2Value.Should().BeNull(); // L2 should not have the value

        await StopHostedServicesAsync(serviceProvider);
        await DisposeServiceProviderAsync(serviceProvider);
    }

    [Fact]
    public async Task HybridCache_TagInvalidation_ShouldInvalidateBothCaches()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        // Add Redis infrastructure
        services.AddRedisHybridInfrastructureForTests(
            redisOptions =>
            {
                redisOptions.ConnectionString = RedisConnectionString;
                redisOptions.KeyPrefix = CreateKeyPrefix("invalidate-test");
            },
            storageOptions =>
            {
                storageOptions.L2Enabled = true;
            });
        services.Configure<MethodCache.Core.Storage.HybridCacheOptions>(hybridOptions =>
        {
            hybridOptions.Strategy = MethodCache.Core.Storage.HybridStrategy.WriteThrough;
            hybridOptions.L2Enabled = true;
        });

        var serviceProvider = services.BuildServiceProvider();
        await StartHostedServicesAsync(serviceProvider);
        var cacheManager = serviceProvider.GetRequiredService<ICacheManager>();
        var hybridManager = serviceProvider.GetRequiredService<MethodCache.Core.Storage.IHybridCacheManager>();
        var keyGenerator = serviceProvider.GetRequiredService<ICacheKeyGenerator>();

        var methodName = "TagInvalidateTest";
        var args = new object[] { "tagged-key" };
        var settings = CacheRuntimePolicy.FromPolicy("test", CachePolicy.Empty with
        {
            Duration = TimeSpan.FromMinutes(5),
            Tags = new List<string> { "test-tag", "invalidate-tag" }
        }, CachePolicyFields.Duration | CachePolicyFields.Tags);

        // Store value in cache
        await cacheManager.GetOrCreateAsync(
            methodName,
            args,
            () => Task.FromResult("TaggedValue"),
            settings,
            keyGenerator);

        var cacheKey = keyGenerator.GenerateKey(methodName, args, settings);
        
        // Verify value exists in both caches
        var l1ValueBefore = await hybridManager.GetFromL1Async<string>(cacheKey);
        l1ValueBefore.Should().Be("TaggedValue");

        // Act - Invalidate by tags
        await cacheManager.InvalidateByTagsAsync("test-tag");

        // Assert - Both caches should be invalidated
        var l1ValueAfter = await hybridManager.GetFromL1Async<string>(cacheKey);
        l1ValueAfter.Should().BeNull();

        await StopHostedServicesAsync(serviceProvider);
        await DisposeServiceProviderAsync(serviceProvider);
    }

    [Fact]
    public async Task HybridCache_GetStatsAsync_ShouldReturnAccurateStatistics()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        // Add Redis infrastructure
        services.AddRedisHybridInfrastructureForTests(
            redisOptions =>
            {
                redisOptions.ConnectionString = RedisConnectionString;
                redisOptions.KeyPrefix = CreateKeyPrefix("stats-test");
            },
            storageOptions =>
            {
                storageOptions.L2Enabled = true;
            });
        services.Configure<MethodCache.Core.Storage.HybridCacheOptions>(hybridOptions =>
        {
            hybridOptions.Strategy = MethodCache.Core.Storage.HybridStrategy.WriteThrough;
            hybridOptions.L2Enabled = true;
        });

        var serviceProvider = services.BuildServiceProvider();
        await StartHostedServicesAsync(serviceProvider);
        var cacheManager = serviceProvider.GetRequiredService<ICacheManager>();
        var hybridManager = serviceProvider.GetRequiredService<MethodCache.Core.Storage.IHybridCacheManager>();
        var keyGenerator = serviceProvider.GetRequiredService<ICacheKeyGenerator>();

        // Act - Generate some cache activity
        var settings = CacheRuntimePolicy.FromPolicy("test", CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(5) }, CachePolicyFields.Duration);
        
        // Cache miss
        await cacheManager.GetOrCreateAsync("Method1", new object[] { 1 }, () => Task.FromResult("Value1"), settings, keyGenerator);
        
        // Cache hit
        await cacheManager.GetOrCreateAsync("Method1", new object[] { 1 }, () => Task.FromResult("ShouldNotBeCalled"), settings, keyGenerator);
        
        // Another cache miss
        await cacheManager.GetOrCreateAsync("Method2", new object[] { 2 }, () => Task.FromResult("Value2"), settings, keyGenerator);

        var stats = await hybridManager.GetStatsAsync();

        // Assert
        stats.Should().NotBeNull();
        stats.L1Hits.Should().BeGreaterThan(0);
        stats.L1Entries.Should().BeGreaterThan(0);
        stats.L1HitRatio.Should().BeGreaterThan(0);
        stats.OverallHitRatio.Should().BeGreaterThan(0);

        await StopHostedServicesAsync(serviceProvider);
        await DisposeServiceProviderAsync(serviceProvider);
    }

    [Fact] 
    public async Task HybridCache_EvictionPolicy_ShouldEvictCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        // Add Redis infrastructure
        services.AddRedisHybridInfrastructureForTests(
            redisOptions =>
            {
                redisOptions.ConnectionString = RedisConnectionString;
                redisOptions.KeyPrefix = CreateKeyPrefix("eviction-test");
            },
            storageOptions =>
            {
                storageOptions.L2Enabled = true;
            });
        services.Configure<MethodCache.Core.Storage.HybridCacheOptions>(hybridOptions =>
        {
            hybridOptions.Strategy = MethodCache.Core.Storage.HybridStrategy.WriteThrough;
            hybridOptions.L2Enabled = true;
            hybridOptions.L1MaxItems = 3; // Very small cache
            hybridOptions.L1EvictionPolicy = MethodCache.Core.Storage.L1EvictionPolicy.LRU;
        });

        var serviceProvider = services.BuildServiceProvider();
        await StartHostedServicesAsync(serviceProvider);
        var cacheManager = serviceProvider.GetRequiredService<ICacheManager>();
        var hybridManager = serviceProvider.GetRequiredService<MethodCache.Core.Storage.IHybridCacheManager>();
        var keyGenerator = serviceProvider.GetRequiredService<ICacheKeyGenerator>();

        var settings = CacheRuntimePolicy.FromPolicy("test", CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(5) }, CachePolicyFields.Duration);

        // Act - Fill cache beyond capacity
        await cacheManager.GetOrCreateAsync("Method1", new object[] { 1 }, () => Task.FromResult("Value1"), settings, keyGenerator);
        await cacheManager.GetOrCreateAsync("Method2", new object[] { 2 }, () => Task.FromResult("Value2"), settings, keyGenerator);
        await cacheManager.GetOrCreateAsync("Method3", new object[] { 3 }, () => Task.FromResult("Value3"), settings, keyGenerator);
        
        // Access Method1 to make it more recently used
        await cacheManager.GetOrCreateAsync("Method1", new object[] { 1 }, () => Task.FromResult("ShouldNotBeCalled"), settings, keyGenerator);
        
        // Add another item to trigger eviction
        await cacheManager.GetOrCreateAsync("Method4", new object[] { 4 }, () => Task.FromResult("Value4"), settings, keyGenerator);

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

        await StopHostedServicesAsync(serviceProvider);
        await DisposeServiceProviderAsync(serviceProvider);
    }
}
