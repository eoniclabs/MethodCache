using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MethodCache.Providers.SqlServer.HealthChecks;

namespace MethodCache.Providers.SqlServer.IntegrationTests.Tests;

public class SqlServerHealthCheckIntegrationTests : SqlServerIntegrationTestBase
{
    [Fact(Timeout = 30000)] // 30 seconds
    public async Task CheckHealthAsync_WithHealthyDatabase_ShouldReturnHealthy()
    {
        // Arrange
        var healthCheck = ServiceProvider.GetRequiredService<SqlServerInfrastructureHealthCheck>();

        // Act
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        // Assert
        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().ContainKey("database_accessible");
        result.Data["database_accessible"].Should().Be(true);
    }

    [Fact(Timeout = 30000)] // 30 seconds
    public async Task CheckHealthAsync_WithTablesPresent_ShouldIncludeTableInfo()
    {
        // Arrange
        var healthCheck = ServiceProvider.GetRequiredService<SqlServerInfrastructureHealthCheck>();

        // Act
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        // Assert
        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().ContainKey("tables_exist");
        result.Data["tables_exist"].Should().Be(true);
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
        result.Data.Should().ContainKey("backplane_enabled");
        result.Data["backplane_enabled"].Should().Be(true);

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
        result.Exception.Should().NotBeNull();
        result.Data.Should().ContainKey("database_accessible");
        result.Data["database_accessible"].Should().Be(false);

        // Cleanup
        serviceProvider.Dispose();
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
        var healthCheck = ServiceProvider.GetRequiredService<SqlServerInfrastructureHealthCheck>();
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
        var healthCheck = ServiceProvider.GetRequiredService<SqlServerInfrastructureHealthCheck>();

        // Act
        var result1 = await healthCheck.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        var result2 = await healthCheck.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        var result3 = await healthCheck.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        // Assert
        result1.Status.Should().Be(HealthStatus.Healthy);
        result2.Status.Should().Be(HealthStatus.Healthy);
        result3.Status.Should().Be(HealthStatus.Healthy);

        // All should have consistent data
        result1.Data.Should().BeEquivalentTo(result2.Data);
        result2.Data.Should().BeEquivalentTo(result3.Data);
    }
}