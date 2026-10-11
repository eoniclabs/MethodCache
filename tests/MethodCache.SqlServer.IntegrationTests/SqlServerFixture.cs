using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace MethodCache.SqlServer.IntegrationTests;

/// <summary>
/// Finds a SQL Server to test against: <c>METHODCACHE_SQLSERVER_CONNECTION</c> if set, otherwise a Docker
/// container. Without either the tests are skipped, unless <c>METHODCACHE_REQUIRE_INFRA_TESTS</c> is <c>true</c>.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;

    public string? ConnectionString { get; private set; }

    public string? UnavailableReason { get; private set; }

    public async Task InitializeAsync()
    {
        ConnectionString = Environment.GetEnvironmentVariable("METHODCACHE_SQLSERVER_CONNECTION");
        if (!string.IsNullOrEmpty(ConnectionString))
        {
            return;
        }

        try
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await _container.StartAsync();
            ConnectionString = await CreateDatabaseAsync(_container.GetConnectionString());
        }
        catch (Exception ex) when (!InfrastructureRequired)
        {
            UnavailableReason = $"No SQL Server: set METHODCACHE_SQLSERVER_CONNECTION or start Docker ({ex.GetType().Name}).";
        }
    }

    /// <summary>
    /// Creates a database with read committed snapshot isolation, the Azure SQL default, under which readers skip
    /// rows that are still committing instead of waiting for them.
    /// </summary>
    private static async Task<string> CreateDatabaseAsync(string serverConnectionString)
    {
        await using (var connection = new SqlConnection(serverConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "CREATE DATABASE [MethodCacheTests]; ALTER DATABASE [MethodCacheTests] SET READ_COMMITTED_SNAPSHOT ON;",
                connection);
            await command.ExecuteNonQueryAsync();
        }

        return new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = "MethodCacheTests" }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }

    public string RequireConnectionString()
    {
        Skip.If(UnavailableReason != null, UnavailableReason);
        return ConnectionString!;
    }

    private static bool InfrastructureRequired =>
        string.Equals(Environment.GetEnvironmentVariable("METHODCACHE_REQUIRE_INFRA_TESTS"), "true", StringComparison.OrdinalIgnoreCase);
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}
