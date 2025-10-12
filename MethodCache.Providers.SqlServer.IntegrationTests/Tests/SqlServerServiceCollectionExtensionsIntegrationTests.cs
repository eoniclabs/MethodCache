using FluentAssertions;
using MethodCache.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Core.Storage;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Core.Storage.Coordination;
using MethodCache.Providers.SqlServer.Extensions;
using MethodCache.Providers.SqlServer.Infrastructure;
using MethodCache.Providers.SqlServer.Services;

namespace MethodCache.Providers.SqlServer.IntegrationTests.Tests;

public class SqlServerServiceCollectionExtensionsIntegrationTests : SqlServerIntegrationTestBase
{
    [Fact]
    public async Task AddSqlServerInfrastructure_ShouldRegisterAllRequiredServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddSqlServerInfrastructure(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
        });

        var serviceProvider = services.BuildServiceProvider();

        // Assert
        serviceProvider.GetService<ISqlServerConnectionManager>().Should().NotBeNull();
        serviceProvider.GetService<ISqlServerSerializer>().Should().NotBeNull();
        serviceProvider.GetService<ISqlServerTableManager>().Should().NotBeNull();
        serviceProvider.GetService<SqlServerPersistentStorageProvider>().Should().NotBeNull();
        serviceProvider.GetService<IStorageProvider>().Should().NotBeNull();
        serviceProvider.GetService<SqlServerBackplane>().Should().NotBeNull();
        serviceProvider.GetService<IBackplane>().Should().NotBeNull();

        // Verify they are the same instances
        serviceProvider.GetService<IStorageProvider>().Should().BeOfType<SqlServerPersistentStorageProvider>();
        serviceProvider.GetService<IBackplane>().Should().BeOfType<SqlServerBackplane>();

        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task AddSqlServerHybridInfrastructure_ShouldRegisterHybridServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddSqlServerHybridInfrastructure(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
        });

        var serviceProvider = services.BuildServiceProvider();

        // Assert
        serviceProvider.GetService<IStorageProvider>().Should().NotBeNull();
        serviceProvider.GetService<IBackplane>().Should().NotBeNull();
        // Hybrid storage manager should be registered
        serviceProvider.GetService<StorageCoordinator>().Should().NotBeNull();

        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task AddSqlServerInfrastructureWithHealthChecks_ShouldRegisterHealthChecks()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddSqlServerInfrastructureWithHealthChecks(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.EnableAutoTableCreation = true;
        }, "test_sql_health");

        var serviceProvider = services.BuildServiceProvider();

        // Ensure tables exist first
        var tableManager = serviceProvider.GetRequiredService<ISqlServerTableManager>();
        await tableManager.EnsureTablesExistAsync();

        // Assert
        var healthCheckService = serviceProvider.GetService<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>();
        healthCheckService.Should().NotBeNull();

        var healthCheck = serviceProvider.GetService<MethodCache.Providers.SqlServer.HealthChecks.SqlServerInfrastructureHealthCheck>();
        healthCheck.Should().NotBeNull();

        // Test health check
        var result = await healthCheckService!.CheckHealthAsync();
        result.Status.Should().Be(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy);

        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task AddSqlServerCache_ShouldRegisterMethodCacheAndSqlServer()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddSqlServerCache(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
        });

        var serviceProvider = services.BuildServiceProvider();

        // Assert
        // Should have MethodCache core services
        serviceProvider.GetService<ICacheManager>().Should().NotBeNull();

        // Should have SQL Server infrastructure
        serviceProvider.GetService<IStorageProvider>().Should().NotBeNull();
        serviceProvider.GetService<IBackplane>().Should().NotBeNull();

        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task AddHybridSqlServerCache_WithOptions_ShouldRegisterHybridCache()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddHybridSqlServerCache(hybridOptions =>
        {
            hybridOptions.L1DefaultExpiration = TimeSpan.FromMinutes(5);
            hybridOptions.L2DefaultExpiration = TimeSpan.FromHours(1);
        }, sqlOptions =>
        {
            sqlOptions.ConnectionString = SqlServerConnectionString;
        });

        var serviceProvider = services.BuildServiceProvider();

        // Assert
        serviceProvider.GetService<IStorageProvider>().Should().NotBeNull();
        serviceProvider.GetService<IBackplane>().Should().NotBeNull();
        serviceProvider.GetService<StorageCoordinator>().Should().NotBeNull();

        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task AddHybridSqlServerCache_WithConnectionString_ShouldRegisterServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddHybridSqlServerCache(SqlServerConnectionString);

        var serviceProvider = services.BuildServiceProvider();

        // Assert
        serviceProvider.GetService<IStorageProvider>().Should().NotBeNull();
        serviceProvider.GetService<IBackplane>().Should().NotBeNull();
        serviceProvider.GetService<StorageCoordinator>().Should().NotBeNull();

        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task AddSqlServerHybridCacheComplete_ShouldRegisterCompleteStack()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddSqlServerHybridCacheComplete(SqlServerConnectionString, options =>
        {
            options.L1DefaultExpiration = TimeSpan.FromMinutes(10);
            options.L2DefaultExpiration = TimeSpan.FromHours(2);
            options.EnableBackplane = true;
            options.Schema = $"complete_{Guid.NewGuid():N}".Replace("-", "");
        });

        var serviceProvider = services.BuildServiceProvider();

        // Assert
        serviceProvider.GetService<IStorageProvider>().Should().NotBeNull();
        serviceProvider.GetService<IBackplane>().Should().NotBeNull();
        serviceProvider.GetService<StorageCoordinator>().Should().NotBeNull();

        // Verify configuration was applied
        var sqlOptions = serviceProvider.GetService<Microsoft.Extensions.Options.IOptions<MethodCache.Providers.SqlServer.Configuration.SqlServerOptions>>();
        sqlOptions.Should().NotBeNull();
        sqlOptions!.Value.ConnectionString.Should().Be(SqlServerConnectionString);
        sqlOptions.Value.EnableBackplane.Should().BeTrue();

        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task EndToEndIntegration_ShouldWorkWithRealDatabase()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddSqlServerHybridCacheComplete(SqlServerConnectionString, options =>
        {
            options.EnableBackplane = true;
            options.EnableAutoTableCreation = true;
            options.Schema = $"endtoend_{Guid.NewGuid():N}".Replace("-", "");
        });

        var serviceProvider = services.BuildServiceProvider();

        // Initialize tables
        var tableManager = serviceProvider.GetRequiredService<ISqlServerTableManager>();
        await tableManager.EnsureTablesExistAsync();

        var storageProvider = serviceProvider.GetRequiredService<IStorageProvider>();

        // Act & Assert
        // Test basic storage
        await storageProvider.SetAsync("test-key", "test-value", TimeSpan.FromMinutes(5));
        var retrieved = await storageProvider.GetAsync<string>("test-key");
        retrieved.Should().Be("test-value");

        // Test with tags
        await storageProvider.SetAsync("tagged-key", "tagged-value", TimeSpan.FromMinutes(5), new[] { "test-tag" });
        var taggedRetrieved = await storageProvider.GetAsync<string>("tagged-key");
        taggedRetrieved.Should().Be("tagged-value");

        // Test tag-based removal
        await storageProvider.RemoveByTagAsync("test-tag");
        var afterTagRemoval = await storageProvider.GetAsync<string>("tagged-key");
        afterTagRemoval.Should().BeNull();

        // Test backplane
        var backplane = serviceProvider.GetRequiredService<IBackplane>();
        await backplane.PublishInvalidationAsync("backplane-test");
        await backplane.PublishTagInvalidationAsync("backplane-tag");

        // Cleanup
        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task MultipleRegistrations_ShouldUseSingletonPattern()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddSqlServerInfrastructure(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
        });

        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var storage1 = serviceProvider.GetRequiredService<IStorageProvider>();
        var storage2 = serviceProvider.GetRequiredService<IStorageProvider>();
        var backplane1 = serviceProvider.GetRequiredService<IBackplane>();
        var backplane2 = serviceProvider.GetRequiredService<IBackplane>();

        storage1.Should().BeSameAs(storage2);
        backplane1.Should().BeSameAs(backplane2);

        await serviceProvider.DisposeAsync();
    }
}