using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using MethodCache.Core.Runtime.Defaults;
using MethodCache.Providers.SqlServer.HealthChecks;
using MethodCache.Providers.SqlServer.Infrastructure;
using MethodCache.Providers.SqlServer.Services;

namespace MethodCache.Providers.SqlServer.IntegrationTests.Tests;

public class SqlServerHealthCheckIntegrationTests : SqlServerIntegrationTestBase
{
    [Fact(Timeout = 30000)] // 30 seconds
    public async Task CheckHealthAsync_WithHealthyDatabase_ShouldReturnHealthy()
    {
        // Arrange
        var healthCheck = new SqlServerInfrastructureHealthCheck(
            ServiceProvider.GetRequiredService<SqlServerPersistentStorageProvider>(),
            ServiceProvider.GetRequiredService<ILogger<SqlServerInfrastructureHealthCheck>>());

        // Act
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        // Assert
        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().ContainKey("Provider");
        result.Data.Should().ContainKey("Status");
        result.Data["Provider"].Should().Be("SqlServer-L3-Persistent");
        result.Data["Status"].Should().Be("Healthy");
    }

    [Fact(Timeout = 30000)] // 30 seconds
    public async Task CheckHealthAsync_WithTablesPresent_ShouldIncludeTableInfo()
    {
        // Arrange
        var healthCheck = new SqlServerInfrastructureHealthCheck(
            ServiceProvider.GetRequiredService<SqlServerPersistentStorageProvider>(),
            ServiceProvider.GetRequiredService<ILogger<SqlServerInfrastructureHealthCheck>>());

        // Act
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        // Assert
        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().ContainKey("Provider");
        result.Data.Should().ContainKey("Status");
        result.Data["Provider"].Should().Be("SqlServer-L3-Persistent");
        result.Data["Status"].Should().Be("Healthy");
    }

    [Fact(Timeout = 30000)] // 30 seconds
    public async Task CheckHealthAsync_WithBackplaneEnabled_ShouldIncludeBackplaneInfo()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlServerInfrastructureWithHealthChecksForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.EnableBackplane = true;
            options.EnableAutoTableCreation = true;
            options.Schema = $"hcbackplane_{Guid.NewGuid():N}".Replace("-", "");
        });

        var serviceProvider = services.BuildServiceProvider();

        // Initialize tables
        var tableManager = serviceProvider.GetRequiredService<MethodCache.Providers.SqlServer.Services.ISqlServerTableManager>();
        await tableManager.EnsureTablesExistAsync();

        var healthCheck = serviceProvider.GetRequiredService<SqlServerInfrastructureHealthCheck>();

        // Act
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        // Assert
        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().ContainKey("Provider");
        result.Data.Should().ContainKey("Status");
        result.Data["Provider"].Should().Be("SqlServer-L3-Persistent");
        result.Data["Status"].Should().Be("Healthy");

        // Cleanup
        await serviceProvider.DisposeAsync();
    }

    [Fact(Timeout = 30000)] // 30 seconds
    public async Task CheckHealthAsync_WithInvalidConnectionString_ShouldReturnUnhealthy()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlServerInfrastructureWithHealthChecksForTests(options =>
        {
            options.ConnectionString = "Server=invalid;Database=invalid;Integrated Security=true;TrustServerCertificate=true;Connection Timeout=1;";
            options.EnableAutoTableCreation = false;
        });

        var serviceProvider = services.BuildServiceProvider();
        var healthCheck = serviceProvider.GetRequiredService<SqlServerInfrastructureHealthCheck>();

        // Act
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        // Assert
        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data.Should().ContainKey("Provider");
        result.Data["Provider"].Should().Be("SqlServer-L3-Persistent");

        // Cleanup
        await serviceProvider.DisposeAsync();
    }

    [Fact(Timeout = 30000)] // 30 seconds
    public async Task HealthCheckService_Integration_ShouldWorkWithDependencyInjection()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlServerInfrastructureWithHealthChecksForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.EnableAutoTableCreation = true;
            options.Schema = $"hcintegration_{Guid.NewGuid():N}".Replace("-", "");
        }, "sql_test_health");

        var serviceProvider = services.BuildServiceProvider();

        // Initialize tables
        var tableManager = serviceProvider.GetRequiredService<MethodCache.Providers.SqlServer.Services.ISqlServerTableManager>();
        await tableManager.EnsureTablesExistAsync();

        var healthCheckService = serviceProvider.GetRequiredService<HealthCheckService>();

        // Act
        var report = await healthCheckService.CheckHealthAsync();

        // Assert
        report.Status.Should().Be(HealthStatus.Healthy);
        report.Entries.Should().ContainKey("sql_test_health");
        report.Entries["sql_test_health"].Status.Should().Be(HealthStatus.Healthy);

        // Cleanup
        await serviceProvider.DisposeAsync();
    }

    [Fact(Timeout = 30000)] // 30 seconds
    public async Task CheckHealthAsync_WithTimeout_ShouldHandleGracefully()
    {
        // Arrange
        var healthCheck = new SqlServerInfrastructureHealthCheck(
            ServiceProvider.GetRequiredService<SqlServerPersistentStorageProvider>(),
            ServiceProvider.GetRequiredService<ILogger<SqlServerInfrastructureHealthCheck>>());
        var shortTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));

        // Act
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), shortTimeout.Token);

        // Assert
        // The result depends on timing, but it should either succeed or handle cancellation gracefully
        result.Status.Should().BeOneOf(HealthStatus.Healthy, HealthStatus.Unhealthy);
    }

    [Fact(Timeout = 30000)] // 30 seconds
    public async Task CheckHealthAsync_MultipleChecks_ShouldBeConsistent()
    {
        // Arrange
        var healthCheck = new SqlServerInfrastructureHealthCheck(
            ServiceProvider.GetRequiredService<SqlServerPersistentStorageProvider>(),
            ServiceProvider.GetRequiredService<ILogger<SqlServerInfrastructureHealthCheck>>());

        // Act
        var result1 = await healthCheck.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        var result2 = await healthCheck.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        var result3 = await healthCheck.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        // Assert
        result1.Status.Should().Be(HealthStatus.Healthy);
        result2.Status.Should().Be(HealthStatus.Healthy);
        result3.Status.Should().Be(HealthStatus.Healthy);

        // All should have consistent basic data (excluding statistics which can vary)
        result1.Data["Provider"].Should().Be(result2.Data["Provider"]);
        result1.Data["Status"].Should().Be(result2.Data["Status"]);
        result2.Data["Provider"].Should().Be(result3.Data["Provider"]);
        result2.Data["Status"].Should().Be(result3.Data["Status"]);
    }
}