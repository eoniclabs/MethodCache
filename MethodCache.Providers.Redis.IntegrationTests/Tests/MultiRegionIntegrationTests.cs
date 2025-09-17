using FluentAssertions;
using MethodCache.Providers.Redis.Extensions;
using MethodCache.Providers.Redis.MultiRegion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace MethodCache.Providers.Redis.IntegrationTests.Tests;

public class MultiRegionIntegrationTests : RedisIntegrationTestBase
{
    [Fact]
    public async Task MultiRegion_GetFromRegionAsync_ShouldRetrieveFromSpecificRegion()
    {
        // Arrange - Create multi-region setup with single Redis instance simulating multiple regions
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        services.AddMultiRegionRedisCache(options =>
        {
            // Use the same Redis connection for all regions (since we're testing multi-region logic, not actual Redis separation)
            var baseConnectionString = RedisConnectionString;
            options.AddRegion("us-east-1", baseConnectionString, isPrimary: true, priority: 10, replicationStrategy: RegionReplicationStrategy.None)
                   .AddRegion("us-west-2", baseConnectionString, priority: 8, replicationStrategy: RegionReplicationStrategy.None)
                   .AddRegion("eu-west-1", baseConnectionString, priority: 5, replicationStrategy: RegionReplicationStrategy.None)
                   .WithFailoverStrategy(RegionFailoverStrategy.PriorityBased)
                   .EnableCrossRegionInvalidation();
        });

        var serviceProvider = services.BuildServiceProvider();
        await StartHostedServicesAsync(serviceProvider);
        var multiRegionManager = serviceProvider.GetRequiredService<IMultiRegionCacheManager>();

        var testData = new TestObject { Id = 123, Name = "Multi-Region Test", Value = "Test Data" };
        var key = CreateKey("multi-region-test-key");
        var expiration = TimeSpan.FromMinutes(5);

        // Act - Set data in specific region
        await multiRegionManager.SetInRegionAsync(key, testData, expiration, "us-east-1");

        // Get data from the same region
        var result = await multiRegionManager.GetFromRegionAsync<TestObject>(key, "us-east-1");

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(testData.Id);
        result.Name.Should().Be(testData.Name);
        result.Value.Should().Be(testData.Value);

        // Verify data is not in other regions
        var resultWest = await multiRegionManager.GetFromRegionAsync<TestObject>(key, "us-west-2");
        var resultEu = await multiRegionManager.GetFromRegionAsync<TestObject>(key, "eu-west-1");

        resultWest.Should().BeNull();
        resultEu.Should().BeNull();

        await StopHostedServicesAsync(serviceProvider);
        await DisposeServiceProviderAsync(serviceProvider);
    }

    [Fact]
    public async Task MultiRegion_GetFromMultipleRegionsAsync_ShouldReturnDataFromAllRegions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        services.AddMultiRegionRedisCache(options =>
        {
            var baseConnectionString = RedisConnectionString;
            options.AddRegion("region-1", baseConnectionString, isPrimary: true, replicationStrategy: RegionReplicationStrategy.None)
                   .AddRegion("region-2", baseConnectionString, replicationStrategy: RegionReplicationStrategy.None)
                   .AddRegion("region-3", baseConnectionString, replicationStrategy: RegionReplicationStrategy.None);
        });

        var serviceProvider = services.BuildServiceProvider();
        await StartHostedServicesAsync(serviceProvider);
        var multiRegionManager = serviceProvider.GetRequiredService<IMultiRegionCacheManager>();

        var key = CreateKey("multi-region-query-key");
        var expiration = TimeSpan.FromMinutes(5);

        // Set different data in different regions
        await multiRegionManager.SetInRegionAsync(key, "Data from Region 1", expiration, "region-1");
        await multiRegionManager.SetInRegionAsync(key, "Data from Region 2", expiration, "region-2");
        // Don't set data in region-3

        // Act
        var regions = new[] { "region-1", "region-2", "region-3" };
        var results = await multiRegionManager.GetFromMultipleRegionsAsync<string>(key, regions);

        // Assert
        results.Should().HaveCount(3);
        results["region-1"].Should().Be("Data from Region 1");
        results["region-2"].Should().Be("Data from Region 2");
        results["region-3"].Should().BeNull();

        await StopHostedServicesAsync(serviceProvider);
        await DisposeServiceProviderAsync(serviceProvider);
    }

    [Fact]
    public async Task MultiRegion_InvalidateGloballyAsync_ShouldInvalidateAcrossAllRegions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        services.AddMultiRegionRedisCache(options =>
        {
            var baseConnectionString = RedisConnectionString;
            options.AddRegion("invalidate-1", baseConnectionString, replicationStrategy: RegionReplicationStrategy.None)
                   .AddRegion("invalidate-2", baseConnectionString, replicationStrategy: RegionReplicationStrategy.None)
                   .AddRegion("invalidate-3", baseConnectionString, replicationStrategy: RegionReplicationStrategy.None);
        });

        var serviceProvider = services.BuildServiceProvider();
        await StartHostedServicesAsync(serviceProvider);
        var multiRegionManager = serviceProvider.GetRequiredService<IMultiRegionCacheManager>();

        var key = CreateKey("global-invalidation-key");
        var testData = "Test data for global invalidation";
        var expiration = TimeSpan.FromMinutes(5);

        // Set data in all regions
        await multiRegionManager.SetInRegionAsync(key, testData, expiration, "invalidate-1");
        await multiRegionManager.SetInRegionAsync(key, testData, expiration, "invalidate-2");
        await multiRegionManager.SetInRegionAsync(key, testData, expiration, "invalidate-3");

        // Verify data exists in all regions
        var beforeInvalidation = await multiRegionManager.GetFromMultipleRegionsAsync<string>(
            key, new[] { "invalidate-1", "invalidate-2", "invalidate-3" });
        
        beforeInvalidation.Values.Should().AllSatisfy(v => v.Should().Be(testData));

        // Act - Global invalidation
        await multiRegionManager.InvalidateGloballyAsync(key);

        // Assert - Data should be removed from all regions
        var afterInvalidation = await multiRegionManager.GetFromMultipleRegionsAsync<string>(
            key, new[] { "invalidate-1", "invalidate-2", "invalidate-3" });
        
        afterInvalidation.Values.Should().AllSatisfy(v => v.Should().BeNull());

        await StopHostedServicesAsync(serviceProvider);
        await DisposeServiceProviderAsync(serviceProvider);
    }

    [Fact]
    public async Task MultiRegion_SyncToRegionAsync_ShouldCopyDataBetweenRegions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        services.AddMultiRegionRedisCache(options =>
        {
            var baseConnectionString = RedisConnectionString;
            options.AddRegion("sync-source", baseConnectionString, replicationStrategy: RegionReplicationStrategy.None)
                   .AddRegion("sync-target", baseConnectionString, replicationStrategy: RegionReplicationStrategy.None);
        });

        var serviceProvider = services.BuildServiceProvider();
        await StartHostedServicesAsync(serviceProvider);
        var multiRegionManager = serviceProvider.GetRequiredService<IMultiRegionCacheManager>();

        var key = CreateKey("sync-test-key");
        var testData = "Data to be synced";
        var expiration = TimeSpan.FromMinutes(5);

        // Set data only in source region
        await multiRegionManager.SetInRegionAsync(key, testData, expiration, "sync-source");

        // Verify data exists in source but not in target
        var sourceData = await multiRegionManager.GetFromRegionAsync<string>(key, "sync-source");
        var targetDataBefore = await multiRegionManager.GetFromRegionAsync<string>(key, "sync-target");

        sourceData.Should().Be(testData);
        targetDataBefore.Should().BeNull();

        // Act - Sync from source to target
        await multiRegionManager.SyncToRegionAsync(key, "sync-source", "sync-target");

        // Assert - Data should now exist in target region
        var targetDataAfter = await multiRegionManager.GetFromRegionAsync<string>(key, "sync-target");
        targetDataAfter.Should().Be(testData);

        await StopHostedServicesAsync(serviceProvider);
        await DisposeServiceProviderAsync(serviceProvider);
    }

    [Fact]
    public async Task MultiRegion_GetRegionHealthAsync_ShouldReturnHealthStatus()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        services.AddMultiRegionRedisCache(options =>
        {
            var baseConnectionString = RedisConnectionString;
            options.AddRegion("health-test", baseConnectionString, isPrimary: true);
        });

        var serviceProvider = services.BuildServiceProvider();
        await StartHostedServicesAsync(serviceProvider);
        var multiRegionManager = serviceProvider.GetRequiredService<IMultiRegionCacheManager>();

        // Allow time for connection initialization
        await Task.Delay(1000);

        // Act
        var healthStatus = await multiRegionManager.GetRegionHealthAsync("health-test");

        // Assert
        healthStatus.Should().NotBeNull();
        healthStatus.Region.Should().Be("health-test");
        healthStatus.IsHealthy.Should().BeTrue();
        healthStatus.Latency.Should().BeGreaterThan(TimeSpan.Zero);
        healthStatus.LastChecked.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(10));
        healthStatus.Metrics.Should().NotBeEmpty();

        await StopHostedServicesAsync(serviceProvider);
        await DisposeServiceProviderAsync(serviceProvider);
    }

    [Fact]
    public async Task MultiRegion_GetAvailableRegionsAsync_ShouldReturnConfiguredRegions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        services.AddMultiRegionRedisCache(options =>
        {
            var baseConnectionString = RedisConnectionString;
            options.AddRegion("available-1", baseConnectionString)
                   .AddRegion("available-2", baseConnectionString)
                   .AddRegion("available-3", baseConnectionString);
        });

        var serviceProvider = services.BuildServiceProvider();
        await StartHostedServicesAsync(serviceProvider);
        var multiRegionManager = serviceProvider.GetRequiredService<IMultiRegionCacheManager>();

        // Act
        var availableRegions = await multiRegionManager.GetAvailableRegionsAsync();

        // Assert
        availableRegions.Should().Contain("available-1");
        availableRegions.Should().Contain("available-2");
        availableRegions.Should().Contain("available-3");
        availableRegions.Should().HaveCount(3);

        await StopHostedServicesAsync(serviceProvider);
        await DisposeServiceProviderAsync(serviceProvider);
    }

    private static string CreateKey(string prefix) => $"{prefix}:{Guid.NewGuid():N}";

    public class TestObject
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}
