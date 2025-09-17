using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MethodCache.Providers.Redis.Extensions;
using MethodCache.Providers.Redis.HealthChecks;
using Xunit;

namespace MethodCache.Providers.Redis.IntegrationTests.Tests;

public class RedisHealthCheckIntegrationTests : RedisIntegrationTestBase
{
    [Fact]
    public async Task RedisHealthCheck_ShouldReturnHealthy_WhenRedisIsAvailable()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRedisCacheWithHealthChecks(options =>
        {
            options.ConnectionString = RedisContainer.GetConnectionString();
        });

        var serviceProvider = services.BuildServiceProvider();
        var healthCheckService = serviceProvider.GetRequiredService<HealthCheckService>();

        // Act
        var result = await healthCheckService.CheckHealthAsync();

        // Assert
        result.Status.Should().Be(HealthStatus.Healthy);
        result.Entries.Should().ContainKey("redis_cache");
        
        var redisCheck = result.Entries["redis_cache"];
        redisCheck.Status.Should().Be(HealthStatus.Healthy);
        redisCheck.Data.Should().ContainKey("ping_ms");
        redisCheck.Data.Should().ContainKey("set_success");
        redisCheck.Data.Should().ContainKey("get_success");
        redisCheck.Data.Should().ContainKey("delete_success");

        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task RedisHealthCheck_ShouldIncludeDetailedInformation()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRedisCacheWithHealthChecks(options =>
        {
            options.ConnectionString = RedisContainer.GetConnectionString();
            options.EnableDistributedLocking = true;
            options.EnablePubSubInvalidation = true;
            options.EnableCacheWarming = true;
            options.KeyPrefix = "health-test:";
        });

        var serviceProvider = services.BuildServiceProvider();
        var healthCheckService = serviceProvider.GetRequiredService<HealthCheckService>();

        // Act
        var result = await healthCheckService.CheckHealthAsync();

        // Assert
        var redisCheck = result.Entries["redis_cache"];
        redisCheck.Data.Should().ContainKey("database_number");
        redisCheck.Data.Should().ContainKey("key_prefix");
        redisCheck.Data.Should().ContainKey("distributed_locking_enabled");
        redisCheck.Data.Should().ContainKey("pubsub_invalidation_enabled");
        redisCheck.Data.Should().ContainKey("cache_warming_enabled");
        
        redisCheck.Data["key_prefix"].Should().Be("health-test:");
        redisCheck.Data["distributed_locking_enabled"].Should().Be(true);
        redisCheck.Data["pubsub_invalidation_enabled"].Should().Be(true);
        redisCheck.Data["cache_warming_enabled"].Should().Be(true);

        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task RedisHealthCheck_WithCustomName_ShouldUseCustomName()
    {
        // Arrange
        var customName = "my_custom_redis_check";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRedisCacheWithHealthChecks(
            options => options.ConnectionString = RedisContainer.GetConnectionString(),
            customName);

        var serviceProvider = services.BuildServiceProvider();
        var healthCheckService = serviceProvider.GetRequiredService<HealthCheckService>();

        // Act
        var result = await healthCheckService.CheckHealthAsync();

        // Assert
        result.Entries.Should().ContainKey(customName);
        result.Entries[customName].Status.Should().Be(HealthStatus.Healthy);

        await serviceProvider.DisposeAsync();
    }
}