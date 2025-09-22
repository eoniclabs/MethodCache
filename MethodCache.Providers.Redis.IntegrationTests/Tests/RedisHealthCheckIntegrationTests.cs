using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MethodCache.Providers.Redis.Infrastructure;
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
        services.AddRedisInfrastructureWithHealthChecksForTests(options =>
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
        result.Entries.Should().ContainKey("redis_infrastructure");

        var redisCheck = result.Entries["redis_infrastructure"];
        redisCheck.Status.Should().Be(HealthStatus.Healthy);
        redisCheck.Data.Should().ContainKey("Provider");
        redisCheck.Data.Should().ContainKey("Status");
        redisCheck.Data.Should().ContainKey("GetOperations");
        redisCheck.Data.Should().ContainKey("SetOperations");

        await StopHostedServicesAsync(serviceProvider);
        await DisposeServiceProviderAsync(serviceProvider);
    }

    [Fact]
    public async Task RedisHealthCheck_ShouldIncludeDetailedInformation()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRedisInfrastructureWithHealthChecksForTests(options =>
        {
            options.ConnectionString = RedisConnectionString;
            options.EnableDistributedLocking = true;
            options.EnablePubSubInvalidation = true;
            options.KeyPrefix = CreateKeyPrefix("health-test");
            options.BackplaneChannel = CreateKeyPrefix("health-test-backplane");
        });

        var serviceProvider = services.BuildServiceProvider();
        await StartHostedServicesAsync(serviceProvider);
        var healthCheckService = serviceProvider.GetRequiredService<HealthCheckService>();

        // Act
        var result = await healthCheckService.CheckHealthAsync();

        // Assert
        var redisCheck = result.Entries["redis_infrastructure"];
        redisCheck.Data.Should().ContainKey("Provider");
        redisCheck.Data.Should().ContainKey("Status");
        redisCheck.Data.Should().ContainKey("GetOperations");
        redisCheck.Data.Should().ContainKey("SetOperations");

        redisCheck.Data["Provider"].Should().Be("Redis");
        redisCheck.Data["Status"].Should().Be("Healthy");

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
        services.AddRedisInfrastructureWithHealthChecksForTests(
            options => options.ConnectionString = RedisConnectionString,
            healthCheckName: customName);

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
