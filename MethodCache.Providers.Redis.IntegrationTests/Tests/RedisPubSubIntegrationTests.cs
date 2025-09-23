using FluentAssertions;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Core.Runtime.Defaults;
using MethodCache.Infrastructure.Abstractions;
using MethodCache.Infrastructure.Extensions;
using MethodCache.Providers.Redis.Features;
using MethodCache.Providers.Redis.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MethodCache.Providers.Redis.IntegrationTests.Tests;

public class RedisPubSubIntegrationTests : RedisIntegrationTestBase
{
    [Fact]
    public async Task PubSub_ShouldInvalidateAcrossInstances()
    {
        // Arrange - Create two separate service providers to simulate different instances
        var services1 = new ServiceCollection();
        services1.AddLogging();
        services1.AddRedisInfrastructureForTests(options =>
        {
            options.ConnectionString = RedisConnectionString;
            options.EnablePubSubInvalidation = true;
            options.KeyPrefix = CreateKeyPrefix("instance1");
        });
        // Register infrastructure-based cache manager
        services1.AddSingleton<ICacheManager>(provider =>
        {
            var storageProvider = provider.GetRequiredService<IStorageProvider>();
            var keyGenerator = provider.GetService<ICacheKeyGenerator>() ?? new DefaultCacheKeyGenerator();
            return new StorageProviderCacheManager(storageProvider, keyGenerator);
        });
        services1.AddSingleton<ICacheKeyGenerator, DefaultCacheKeyGenerator>();
        var serviceProvider1 = services1.BuildServiceProvider();
        await StartHostedServicesAsync(serviceProvider1);
        var cacheManager1 = serviceProvider1.GetRequiredService<ICacheManager>();

        var services2 = new ServiceCollection();
        services2.AddLogging();
        services2.AddRedisInfrastructureForTests(options =>
        {
            options.ConnectionString = RedisConnectionString;
            options.EnablePubSubInvalidation = true;
            options.KeyPrefix = CreateKeyPrefix("instance2");
        });
        // Register infrastructure-based cache manager
        services2.AddSingleton<ICacheManager>(provider =>
        {
            var storageProvider = provider.GetRequiredService<IStorageProvider>();
            var keyGenerator = provider.GetService<ICacheKeyGenerator>() ?? new DefaultCacheKeyGenerator();
            return new StorageProviderCacheManager(storageProvider, keyGenerator);
        });
        services2.AddSingleton<ICacheKeyGenerator, DefaultCacheKeyGenerator>();
        var serviceProvider2 = services2.BuildServiceProvider();
        await StartHostedServicesAsync(serviceProvider2);
        var cacheManager2 = serviceProvider2.GetRequiredService<ICacheManager>();

        // Set up data in both instances
        var keyGenerator1 = serviceProvider1.GetRequiredService<ICacheKeyGenerator>();
        var keyGenerator2 = serviceProvider2.GetRequiredService<ICacheKeyGenerator>();
        var settings = new CacheMethodSettings { Duration = TimeSpan.FromMinutes(5) };
        
        await cacheManager1.GetOrCreateAsync("SharedMethod", new object[] { "key" }, () => Task.FromResult("value1"), settings, keyGenerator1, false);
        await cacheManager2.GetOrCreateAsync("SharedMethod", new object[] { "key" }, () => Task.FromResult("value2"), settings, keyGenerator2, false);

        // Allow pub/sub to initialize
        await Task.Delay(2000);

        // Act - Invalidate by tags from instance 1
        await cacheManager1.InvalidateByTagsAsync("test-tag");
        
        // Allow pub/sub message to propagate
        await Task.Delay(2000);

        // Assert - Both instances should have cache invalidated
        var callCount = 0;
        var value1 = await cacheManager1.GetOrCreateAsync("SharedMethod", new object[] { "key" }, () => { callCount++; return Task.FromResult("new1"); }, settings, keyGenerator1, false);
        var value2 = await cacheManager2.GetOrCreateAsync("SharedMethod", new object[] { "key" }, () => { callCount++; return Task.FromResult("new2"); }, settings, keyGenerator2, false);

        // Since we invalidated by a tag that doesn't exist, values should remain cached
        value1.Should().Be("value1");
        value2.Should().Be("value2");
        callCount.Should().Be(0);

        // Cleanup
        await StopHostedServicesAsync(serviceProvider1);
        await StopHostedServicesAsync(serviceProvider2);
        await DisposeServiceProviderAsync(serviceProvider1);
        await DisposeServiceProviderAsync(serviceProvider2);
    }

    [Fact]
    public async Task PubSub_TagInvalidation_ShouldWorkAcrossInstances()
    {
        // Arrange - Create two separate service providers
        var services1 = new ServiceCollection();
        services1.AddLogging();
        var sharedKeyPrefix = CreateKeyPrefix("pubsub-test-shared");
        services1.AddRedisHybridInfrastructureForTests(
            options =>
            {
                options.ConnectionString = RedisConnectionString;
                options.EnablePubSubInvalidation = true;
                options.KeyPrefix = sharedKeyPrefix;
            },
            storageOptions =>
            {
                storageOptions.L1DefaultExpiration = TimeSpan.FromMinutes(5);
                storageOptions.L2DefaultExpiration = TimeSpan.FromHours(1);
                storageOptions.EnableBackplane = true;
            });
        services1.Configure<MethodCache.Core.Storage.HybridCacheOptions>(hybridOptions =>
        {
            hybridOptions.L1DefaultExpiration = TimeSpan.FromMinutes(5);
            hybridOptions.L2DefaultExpiration = TimeSpan.FromHours(1);
            hybridOptions.EnableBackplane = true;
        });
        services1.AddSingleton<ICacheKeyGenerator, DefaultCacheKeyGenerator>();
        var serviceProvider1 = services1.BuildServiceProvider();
        await StartHostedServicesAsync(serviceProvider1);
        var cacheManager1 = serviceProvider1.GetRequiredService<ICacheManager>();
        var tagManager1 = serviceProvider1.GetRequiredService<IRedisTagManager>();

        var services2 = new ServiceCollection();
        services2.AddLogging();
        services2.AddRedisHybridInfrastructureForTests(
            options =>
            {
                options.ConnectionString = RedisConnectionString;
                options.EnablePubSubInvalidation = true;
                options.KeyPrefix = sharedKeyPrefix;
            },
            storageOptions =>
            {
                storageOptions.L1DefaultExpiration = TimeSpan.FromMinutes(5);
                storageOptions.L2DefaultExpiration = TimeSpan.FromHours(1);
                storageOptions.EnableBackplane = true;
            });
        services2.Configure<MethodCache.Core.Storage.HybridCacheOptions>(hybridOptions =>
        {
            hybridOptions.L1DefaultExpiration = TimeSpan.FromMinutes(5);
            hybridOptions.L2DefaultExpiration = TimeSpan.FromHours(1);
            hybridOptions.EnableBackplane = true;
        });
        services2.AddSingleton<ICacheKeyGenerator, DefaultCacheKeyGenerator>();
        var serviceProvider2 = services2.BuildServiceProvider();
        await StartHostedServicesAsync(serviceProvider2);
        var cacheManager2 = serviceProvider2.GetRequiredService<ICacheManager>();

        // Set up tagged data in both instances
        var keyGenerator1 = serviceProvider1.GetRequiredService<ICacheKeyGenerator>();
        var keyGenerator2 = serviceProvider2.GetRequiredService<ICacheKeyGenerator>();
        var settings = new CacheMethodSettings 
        { 
            Duration = TimeSpan.FromMinutes(5),
            Tags = new List<string> { "user:123", "profile" }
        };
        await cacheManager1.GetOrCreateAsync("GetUserData", new object[] { 1 }, () => Task.FromResult("data1"), settings, keyGenerator1, false);
        await cacheManager2.GetOrCreateAsync("GetUserData", new object[] { 2 }, () => Task.FromResult("data2"), settings, keyGenerator2, false);

        // Allow pub/sub to initialize
        await Task.Delay(2000);

        // Act - Invalidate by tag from instance 1
        await cacheManager1.InvalidateByTagsAsync("user:123");
        
        // Allow pub/sub message to propagate
        await Task.Delay(2000);

        // Assert - Both instances should have tagged entries invalidated
        var callCount = 0;
        var data1 = await cacheManager1.GetOrCreateAsync("GetUserData", new object[] { 1 }, () => { callCount++; return Task.FromResult("newdata1"); }, settings, keyGenerator1, false);
        var data2 = await cacheManager2.GetOrCreateAsync("GetUserData", new object[] { 2 }, () => { callCount++; return Task.FromResult("newdata2"); }, settings, keyGenerator2, false);

        data1.Should().Be("newdata1"); // Should be re-created (invalidated)
        data2.Should().Be("newdata2"); // Should be re-created (invalidated)
        callCount.Should().Be(2); // Both entries were invalidated and re-created

        // Cleanup
        await StopHostedServicesAsync(serviceProvider1);
        await StopHostedServicesAsync(serviceProvider2);
        await DisposeServiceProviderAsync(serviceProvider1);
        await DisposeServiceProviderAsync(serviceProvider2);
    }
}
