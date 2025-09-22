using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Providers.SqlServer.Services;

namespace MethodCache.Providers.SqlServer.IntegrationTests.Tests;

public class SqlServerTableManagerIntegrationTests : SqlServerIntegrationTestBase
{
    [Fact]
    public async Task EnsureTablesExistAsync_ShouldCreateAllRequiredTables()
    {
        // Arrange
        var tableManager = ServiceProvider.GetRequiredService<ISqlServerTableManager>();

        // Act
        await tableManager.EnsureTablesExistAsync();

        // Assert
        var tablesExist = await tableManager.TablesExistAsync();
        tablesExist.Should().BeTrue();
    }

    [Fact]
    public async Task TablesExistAsync_WithoutTables_ShouldReturnFalse()
    {
        // Arrange - Create a new table manager with a different schema that doesn't exist
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlServerInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.Schema = $"nonexistent_{Guid.NewGuid():N}".Replace("-", "");
            options.EnableAutoTableCreation = false; // Don't auto-create
        });

        var serviceProvider = services.BuildServiceProvider();
        var tableManager = serviceProvider.GetRequiredService<ISqlServerTableManager>();

        // Act
        var tablesExist = await tableManager.TablesExistAsync();

        // Assert
        tablesExist.Should().BeFalse();

        // Cleanup
        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task CreateSchemaAsync_ShouldCreateSchemaIfNotExists()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var newSchema = $"testschema_{Guid.NewGuid():N}".Replace("-", "");
        services.AddSqlServerInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.Schema = newSchema;
            options.EnableAutoTableCreation = false;
        });

        var serviceProvider = services.BuildServiceProvider();
        var tableManager = serviceProvider.GetRequiredService<ISqlServerTableManager>();

        // Act
        await tableManager.CreateSchemaAsync();

        // Assert - Schema should now exist
        // We can verify this by trying to create tables in it
        await tableManager.CreateEntriesTableAsync();

        // If no exception is thrown, schema creation was successful

        // Cleanup
        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task CreateEntriesTableAsync_ShouldCreateEntriesTable()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var newSchema = $"entriestest_{Guid.NewGuid():N}".Replace("-", "");
        services.AddSqlServerInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.Schema = newSchema;
            options.EnableAutoTableCreation = false;
        });

        var serviceProvider = services.BuildServiceProvider();
        var tableManager = serviceProvider.GetRequiredService<ISqlServerTableManager>();

        // Act
        await tableManager.CreateSchemaAsync();
        await tableManager.CreateEntriesTableAsync();

        // Assert - Try to use the table with a basic operation
        var connectionManager = serviceProvider.GetRequiredService<MethodCache.Providers.SqlServer.Services.ISqlServerConnectionManager>();
        await using var connection = await connectionManager.GetConnectionAsync();

        var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MethodCache.Providers.SqlServer.Configuration.SqlServerOptions>>().Value;
        var checkSql = $"SELECT COUNT(*) FROM {options.FullEntriesTableName}";

        await using var command = new Microsoft.Data.SqlClient.SqlCommand(checkSql, connection);
        var count = await command.ExecuteScalarAsync();

        count.Should().NotBeNull(); // If table exists, this will return 0, not null

        // Cleanup
        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task CreateTagsTableAsync_ShouldCreateTagsTableWithForeignKey()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var newSchema = $"tagstest_{Guid.NewGuid():N}".Replace("-", "");
        services.AddSqlServerInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.Schema = newSchema;
            options.EnableAutoTableCreation = false;
        });

        var serviceProvider = services.BuildServiceProvider();
        var tableManager = serviceProvider.GetRequiredService<ISqlServerTableManager>();

        // Act
        await tableManager.CreateSchemaAsync();
        await tableManager.CreateEntriesTableAsync(); // Must create entries table first due to FK
        await tableManager.CreateTagsTableAsync();

        // Assert - Try to use the table
        var connectionManager = serviceProvider.GetRequiredService<MethodCache.Providers.SqlServer.Services.ISqlServerConnectionManager>();
        await using var connection = await connectionManager.GetConnectionAsync();

        var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MethodCache.Providers.SqlServer.Configuration.SqlServerOptions>>().Value;
        var checkSql = $"SELECT COUNT(*) FROM {options.FullTagsTableName}";

        await using var command = new Microsoft.Data.SqlClient.SqlCommand(checkSql, connection);
        var count = await command.ExecuteScalarAsync();

        count.Should().NotBeNull(); // If table exists, this will return 0, not null

        // Cleanup
        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task CreateInvalidationsTableAsync_ShouldCreateInvalidationsTable()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var newSchema = $"invalidtest_{Guid.NewGuid():N}".Replace("-", "");
        services.AddSqlServerInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.Schema = newSchema;
            options.EnableAutoTableCreation = false;
        });

        var serviceProvider = services.BuildServiceProvider();
        var tableManager = serviceProvider.GetRequiredService<ISqlServerTableManager>();

        // Act
        await tableManager.CreateSchemaAsync();
        await tableManager.CreateInvalidationsTableAsync();

        // Assert - Try to use the table
        var connectionManager = serviceProvider.GetRequiredService<MethodCache.Providers.SqlServer.Services.ISqlServerConnectionManager>();
        await using var connection = await connectionManager.GetConnectionAsync();

        var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MethodCache.Providers.SqlServer.Configuration.SqlServerOptions>>().Value;
        var checkSql = $"SELECT COUNT(*) FROM {options.FullInvalidationsTableName}";

        await using var command = new Microsoft.Data.SqlClient.SqlCommand(checkSql, connection);
        var count = await command.ExecuteScalarAsync();

        count.Should().NotBeNull(); // If table exists, this will return 0, not null

        // Cleanup
        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task CreateIndexesAsync_ShouldCreatePerformanceIndexes()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var newSchema = $"indextest_{Guid.NewGuid():N}".Replace("-", "");
        services.AddSqlServerInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.Schema = newSchema;
            options.EnableAutoTableCreation = false;
        });

        var serviceProvider = services.BuildServiceProvider();
        var tableManager = serviceProvider.GetRequiredService<ISqlServerTableManager>();

        // Act
        await tableManager.CreateSchemaAsync();
        await tableManager.CreateEntriesTableAsync();
        await tableManager.CreateTagsTableAsync();
        await tableManager.CreateInvalidationsTableAsync();
        await tableManager.CreateIndexesAsync();

        // Assert - Check if indexes exist by querying system tables
        var connectionManager = serviceProvider.GetRequiredService<MethodCache.Providers.SqlServer.Services.ISqlServerConnectionManager>();
        await using var connection = await connectionManager.GetConnectionAsync();

        var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MethodCache.Providers.SqlServer.Configuration.SqlServerOptions>>().Value;

        var checkIndexSql = @"
            SELECT COUNT(*)
            FROM sys.indexes i
            INNER JOIN sys.tables t ON i.object_id = t.object_id
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = @Schema
            AND i.name LIKE 'IX_%'";

        await using var command = new Microsoft.Data.SqlClient.SqlCommand(checkIndexSql, connection);
        command.Parameters.AddWithValue("@Schema", options.Schema);
        var indexCount = (int)(await command.ExecuteScalarAsync() ?? 0);

        indexCount.Should().BeGreaterThan(0); // Should have created at least one index

        // Cleanup
        await serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task FullTableCreation_ShouldCreateAllTablesAndIndexes()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var newSchema = $"fulltest_{Guid.NewGuid():N}".Replace("-", "");
        services.AddSqlServerInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.Schema = newSchema;
            options.EnableAutoTableCreation = true; // This should create everything
        });

        var serviceProvider = services.BuildServiceProvider();
        var tableManager = serviceProvider.GetRequiredService<ISqlServerTableManager>();

        // Act
        await tableManager.EnsureTablesExistAsync();

        // Assert
        var tablesExist = await tableManager.TablesExistAsync();
        tablesExist.Should().BeTrue();

        // Verify we can perform basic operations
        var storageProvider = serviceProvider.GetRequiredService<MethodCache.Infrastructure.Abstractions.IStorageProvider>();
        await storageProvider.SetAsync("test-key", "test-value", TimeSpan.FromMinutes(5));
        var retrieved = await storageProvider.GetAsync<string>("test-key");
        retrieved.Should().Be("test-value");

        // Cleanup
        await serviceProvider.DisposeAsync();
    }
}