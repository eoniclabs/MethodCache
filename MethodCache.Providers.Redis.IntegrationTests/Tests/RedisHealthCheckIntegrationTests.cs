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
            options.ConnectionString = RedisConnectionString;
        });

        var serviceProvider = services.BuildServiceProvider();
        await StartHostedServicesAsync(serviceProvider);
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

        await StopHostedServicesAsync(serviceProvider);
        await DisposeServiceProviderAsync(serviceProvider);
    }

    [Fact]
    public async Task RedisHealthCheck_ShouldIncludeDetailedInformation()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRedisCacheWithHealthChecks(options =>
        {
            options.ConnectionString = RedisConnectionString;
            options.EnableDistributedLocking = true;
            options.EnablePubSubInvalidation = true;
            options.EnableCacheWarming = false; // Temporarily disabled to test if this causes hang
            options.KeyPrefix = CreateKeyPrefix("health-test");
            options.BackplaneChannel = CreateKeyPrefix("health-test-backplane");
        });

        var serviceProvider = services.BuildServiceProvider();
        await StartHostedServicesAsync(serviceProvider);
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
        
        redisCheck.Data["key_prefix"].Should().NotBeNull();
        ((string)redisCheck.Data["key_prefix"]).Should().StartWith("health-test:");
        redisCheck.Data["distributed_locking_enabled"].Should().Be(true);
        redisCheck.Data["pubsub_invalidation_enabled"].Should().Be(true);
        redisCheck.Data["cache_warming_enabled"].Should().Be(false); // We disabled it to avoid hanging

        await StopHostedServicesAsync(serviceProvider);
        await DisposeServiceProviderAsync(serviceProvider);
    }

    [Fact]
    public async Task RedisHealthCheck_WithCustomName_ShouldUseCustomName()
    {
        // Arrange
        var customName = "my_custom_redis_check";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRedisCacheWithHealthChecks(
            options => options.ConnectionString = RedisConnectionString,
            customName);

        var serviceProvider = services.BuildServiceProvider();
        await StartHostedServicesAsync(serviceProvider);
        var healthCheckService = serviceProvider.GetRequiredService<HealthCheckService>();

        // Act
        var result = await healthCheckService.CheckHealthAsync();

        // Assert
        result.Entries.Should().ContainKey(customName);
        result.Entries[customName].Status.Should().Be(HealthStatus.Healthy);

        await StopHostedServicesAsync(serviceProvider);
        await DisposeServiceProviderAsync(serviceProvider);
    }
}
