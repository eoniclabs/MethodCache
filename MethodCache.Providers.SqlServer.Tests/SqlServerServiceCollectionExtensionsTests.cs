using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Core.Storage;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Providers.SqlServer.Configuration;
using MethodCache.Providers.SqlServer.Extensions;
using MethodCache.Providers.SqlServer.Infrastructure;
using MethodCache.Providers.SqlServer.Services;

namespace MethodCache.Providers.SqlServer.Tests;

public class SqlServerServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddSqlServerInfrastructure_ShouldRegisterAllRequiredServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddSqlServerInfrastructure();

        // Assert
        var serviceProvider = services.BuildServiceProvider();

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
    public async Task AddSqlServerInfrastructure_WithConfiguration_ShouldApplyOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddSqlServerInfrastructure(options =>
        {
            options.ConnectionString = "test-connection";
            options.Schema = "custom";
            options.EnableBackplane = true;
        });

        // Assert
        var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SqlServerOptions>>();

        options.Value.ConnectionString.Should().Be("test-connection");
        options.Value.Schema.Should().Be("custom");
        options.Value.EnableBackplane.Should().BeTrue();

        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task AddSqlServerHybridInfrastructure_ShouldRegisterHybridServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddSqlServerHybridInfrastructure();

        // Assert
        var serviceProvider = services.BuildServiceProvider();

        serviceProvider.GetService<IStorageProvider>().Should().NotBeNull();
        serviceProvider.GetService<IBackplane>().Should().NotBeNull();
        // Basic infrastructure services should be registered
        serviceProvider.GetService<IMemoryStorage>().Should().NotBeNull();

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
            options.ConnectionString = "test-connection";
        }, "test_sql_health");

        // Assert
        var serviceProvider = services.BuildServiceProvider();

        var healthCheckService = serviceProvider.GetService<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>();
        healthCheckService.Should().NotBeNull();

        var healthCheck = serviceProvider.GetService<MethodCache.Providers.SqlServer.HealthChecks.SqlServerInfrastructureHealthCheck>();
        healthCheck.Should().NotBeNull();

        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task AddSqlServerCache_ShouldRegisterMethodCacheAndSqlServer()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddSqlServerCache();

        // Assert
        var serviceProvider = services.BuildServiceProvider();

        // Should have MethodCache core services
        serviceProvider.GetService<MethodCache.Core.ICacheManager>().Should().NotBeNull();

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
        });

        // Assert
        var serviceProvider = services.BuildServiceProvider();

        serviceProvider.GetService<IStorageProvider>().Should().NotBeNull();
        serviceProvider.GetService<IBackplane>().Should().NotBeNull();
        serviceProvider.GetService<IMemoryStorage>().Should().NotBeNull();

        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task AddHybridSqlServerCache_WithConnectionString_ShouldRegisterServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddHybridSqlServerCache("test-connection-string");

        // Assert
        var serviceProvider = services.BuildServiceProvider();

        serviceProvider.GetService<IStorageProvider>().Should().NotBeNull();
        serviceProvider.GetService<IBackplane>().Should().NotBeNull();
        serviceProvider.GetService<IMemoryStorage>().Should().NotBeNull();

        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task AddSqlServerHybridCacheComplete_ShouldRegisterCompleteStack()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddSqlServerHybridCacheComplete("test-connection", options =>
        {
            options.L1DefaultExpiration = TimeSpan.FromMinutes(10);
            options.L2DefaultExpiration = TimeSpan.FromHours(2);
            options.EnableBackplane = true;
            options.Schema = "complete_test";
        });

        // Assert
        var serviceProvider = services.BuildServiceProvider();

        serviceProvider.GetService<IStorageProvider>().Should().NotBeNull();
        serviceProvider.GetService<IBackplane>().Should().NotBeNull();
        serviceProvider.GetService<IMemoryStorage>().Should().NotBeNull();

        // Verify configuration was applied
        var sqlOptions = serviceProvider.GetService<Microsoft.Extensions.Options.IOptions<SqlServerOptions>>();
        sqlOptions.Should().NotBeNull();
        sqlOptions!.Value.ConnectionString.Should().Be("test-connection");
        sqlOptions.Value.EnableBackplane.Should().BeTrue();
        sqlOptions.Value.Schema.Should().Be("complete_test");

        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task MultipleRegistrations_ShouldUseSingletonPattern()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddSqlServerInfrastructure();

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

    [Fact]
    public async Task AddSqlServerInfrastructure_WithNullConfiguration_ShouldUseDefaults()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddSqlServerInfrastructure(null);

        // Assert
        var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SqlServerOptions>>();

        options.Value.Schema.Should().Be("cache"); // Default value
        options.Value.EnableBackplane.Should().BeFalse(); // Default value

        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task Services_ShouldBeRegisteredAsSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlServerInfrastructure();

        // Act
        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var connectionManager1 = serviceProvider.GetRequiredService<ISqlServerConnectionManager>();
        var connectionManager2 = serviceProvider.GetRequiredService<ISqlServerConnectionManager>();
        connectionManager1.Should().BeSameAs(connectionManager2);

        var serializer1 = serviceProvider.GetRequiredService<ISqlServerSerializer>();
        var serializer2 = serviceProvider.GetRequiredService<ISqlServerSerializer>();
        serializer1.Should().BeSameAs(serializer2);

        var tableManager1 = serviceProvider.GetRequiredService<ISqlServerTableManager>();
        var tableManager2 = serviceProvider.GetRequiredService<ISqlServerTableManager>();
        tableManager1.Should().BeSameAs(tableManager2);

        await serviceProvider.DisposeAsync();
    }
}