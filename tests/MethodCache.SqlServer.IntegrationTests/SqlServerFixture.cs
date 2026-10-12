using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using MethodCache.SqlServer;
using Microsoft.Extensions.Hosting;

namespace MethodCache.SqlServer.IntegrationTests;

/// <summary>
/// Finds a SQL Server to test against: <c>METHODCACHE_SQLSERVER_CONNECTION</c> if set, otherwise a Docker
/// container. Without either the tests are skipped, unless <c>METHODCACHE_REQUIRE_INFRA_TESTS</c> is <c>true</c>.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    internal static async Task WaitForTablesAsync(IEnumerable<IHostedService> hostedServices, CancellationToken cancellationToken = default)
    {
        foreach (var tables in hostedServices.OfType<SqlServerTables>())
            if (tables.Creating is { } creation) await creation.WaitAsync(cancellationToken);
    }

    private MsSqlContainer? _container;

    public string? ConnectionString { get; private set; }

    public string? UnavailableReason { get; private set; }

    public async Task InitializeAsync()
    {
        ConnectionString = Environment.GetEnvironmentVariable("METHODCACHE_SQLSERVER_CONNECTION");
        if (!string.IsNullOrEmpty(ConnectionString))
        {
            await WarmConnectionAsync(ConnectionString);
            return;
        }

        try
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await _container.StartAsync();
            ConnectionString = await CreateDatabaseAsync(_container.GetConnectionString());
            await WarmConnectionAsync(ConnectionString);
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

    private static async Task WarmConnectionAsync(string connectionString)
    {
        // Open/login/TDS initialization happens before the cache's operation deadline is involved.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(deadline.Token);
        await using var command = new SqlCommand("SELECT 1;", connection);
        await command.ExecuteScalarAsync(deadline.Token);
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
