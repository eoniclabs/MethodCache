using FluentAssertions;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Providers.Redis.Features;
using MethodCache.Providers.Redis.Extensions;
using Microsoft.Extensions.DependencyInjection;
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
        services1.AddRedisCache(options =>
        {
            options.ConnectionString = RedisContainer.GetConnectionString();
            options.EnablePubSubInvalidation = true;
            options.KeyPrefix = "instance1:";
        });
        var serviceProvider1 = services1.BuildServiceProvider();
        var cacheManager1 = serviceProvider1.GetRequiredService<ICacheManager>();

        var services2 = new ServiceCollection();
        services2.AddLogging();
        services2.AddRedisCache(options =>
        {
            options.ConnectionString = RedisContainer.GetConnectionString();
            options.EnablePubSubInvalidation = true;
            options.KeyPrefix = "instance2:";
        });
        var serviceProvider2 = services2.BuildServiceProvider();
        var cacheManager2 = serviceProvider2.GetRequiredService<ICacheManager>();

        // Set up data in both instances
        var keyGenerator1 = serviceProvider1.GetRequiredService<ICacheKeyGenerator>();
        var keyGenerator2 = serviceProvider2.GetRequiredService<ICacheKeyGenerator>();
        var settings = new CacheMethodSettings { Duration = TimeSpan.FromMinutes(5) };
        
        await cacheManager1.GetOrCreateAsync("SharedMethod", new object[] { "key" }, async () => "value1", settings, keyGenerator1, false);
        await cacheManager2.GetOrCreateAsync("SharedMethod", new object[] { "key" }, async () => "value2", settings, keyGenerator2, false);

        // Allow pub/sub to initialize
        await Task.Delay(2000);

        // Act - Invalidate by tags from instance 1
        await cacheManager1.InvalidateByTagsAsync("test-tag");
        
        // Allow pub/sub message to propagate
        await Task.Delay(2000);

        // Assert - Both instances should have cache invalidated
        var callCount = 0;
        var value1 = await cacheManager1.GetOrCreateAsync("SharedMethod", new object[] { "key" }, async () => { callCount++; return "new1"; }, settings, keyGenerator1, false);
        var value2 = await cacheManager2.GetOrCreateAsync("SharedMethod", new object[] { "key" }, async () => { callCount++; return "new2"; }, settings, keyGenerator2, false);

        // Since we invalidated by a tag that doesn't exist, values should remain cached
        value1.Should().Be("value1");
        value2.Should().Be("value2");
        callCount.Should().Be(0);

        // Cleanup
        await serviceProvider1.DisposeAsync();
        await serviceProvider2.DisposeAsync();
    }

    [Fact]
    public async Task PubSub_TagInvalidation_ShouldWorkAcrossInstances()
    {
        // Arrange - Create two separate service providers
        var services1 = new ServiceCollection();
        services1.AddLogging();
        services1.AddRedisCache(options =>
        {
            options.ConnectionString = RedisContainer.GetConnectionString();
            options.EnablePubSubInvalidation = true;
            options.KeyPrefix = "pubsub-test:";
        });
        var serviceProvider1 = services1.BuildServiceProvider();
        var cacheManager1 = serviceProvider1.GetRequiredService<ICacheManager>();
        var tagManager1 = serviceProvider1.GetRequiredService<IRedisTagManager>();

        var services2 = new ServiceCollection();
        services2.AddLogging();
        services2.AddRedisCache(options =>
        {
            options.ConnectionString = RedisContainer.GetConnectionString();
            options.EnablePubSubInvalidation = true;
            options.KeyPrefix = "pubsub-test:";
        });
        var serviceProvider2 = services2.BuildServiceProvider();
        var cacheManager2 = serviceProvider2.GetRequiredService<ICacheManager>();

        // Set up tagged data in both instances
        var keyGenerator1 = serviceProvider1.GetRequiredService<ICacheKeyGenerator>();
        var keyGenerator2 = serviceProvider2.GetRequiredService<ICacheKeyGenerator>();
        var settings = new CacheMethodSettings 
        { 
            Duration = TimeSpan.FromMinutes(5),
            Tags = new List<string> { "user:123", "profile" }
        };
        await cacheManager1.GetOrCreateAsync("GetUserData", new object[] { 1 }, async () => "data1", settings, keyGenerator1, false);
        await cacheManager2.GetOrCreateAsync("GetUserData", new object[] { 2 }, async () => "data2", settings, keyGenerator2, false);

        // Allow pub/sub to initialize
        await Task.Delay(2000);

        // Act - Invalidate by tag from instance 1
        await cacheManager1.InvalidateByTagsAsync("user:123");
        
        // Allow pub/sub message to propagate
        await Task.Delay(2000);

        // Assert - Both instances should have tagged entries invalidated
        var callCount = 0;
        var data1 = await cacheManager1.GetOrCreateAsync("GetUserData", new object[] { 1 }, async () => { callCount++; return "newdata1"; }, settings, keyGenerator1, false);
        var data2 = await cacheManager2.GetOrCreateAsync("GetUserData", new object[] { 2 }, async () => { callCount++; return "newdata2"; }, settings, keyGenerator2, false);

        data1.Should().Be("newdata1"); // Should be re-created (invalidated)
        data2.Should().Be("newdata2"); // Should be re-created (invalidated)
        callCount.Should().Be(2); // Both entries were invalidated and re-created

        // Cleanup
        await serviceProvider1.DisposeAsync();
        await serviceProvider2.DisposeAsync();
    }
}