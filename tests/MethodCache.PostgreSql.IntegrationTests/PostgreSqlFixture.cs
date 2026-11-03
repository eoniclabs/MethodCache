using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MethodCache.PostgreSql.IntegrationTests;

/// <summary>
/// Finds a PostgreSQL to test against: <c>METHODCACHE_POSTGRESQL_CONNECTION</c> if set, otherwise a Docker
/// container. Without either the tests are skipped, unless <c>METHODCACHE_REQUIRE_INFRA_TESTS</c> is <c>true</c>.
/// Each test works in a schema of its own.
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string? ConnectionString { get; private set; }

    public string? UnavailableReason { get; private set; }

    public async Task InitializeAsync()
    {
        ConnectionString = Environment.GetEnvironmentVariable("METHODCACHE_POSTGRESQL_CONNECTION");
        if (!string.IsNullOrEmpty(ConnectionString))
        {
            await WarmConnectionAsync(ConnectionString);
            return;
        }

        try
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
            await WarmConnectionAsync(ConnectionString);
        }
        catch (Exception ex) when (!InfrastructureRequired)
        {
            UnavailableReason = $"No PostgreSQL: set METHODCACHE_POSTGRESQL_CONNECTION or start Docker ({ex.GetType().Name}).";
        }
    }

    public async Task DisposeAsync()
    {
        if (_container != null) await _container.DisposeAsync();
    }

    public string RequireConnectionString()
    {
        Skip.If(UnavailableReason != null, UnavailableReason);
        return ConnectionString!;
    }

    /// <summary>Options for a schema no other test uses.</summary>
    public PostgreSqlOptions Options(string name) => new()
    {
        ConnectionString = RequireConnectionString(),
        SchemaName = name + "_" + Guid.NewGuid().ToString("N")
    };

    internal static async Task WaitForTablesAsync(IEnumerable<IHostedService> hostedServices, CancellationToken cancellationToken = default)
    {
        foreach (var tables in hostedServices.OfType<PostgreSqlTables>())
            if (tables.Creating is { } creation) await creation.WaitAsync(cancellationToken);
    }

    internal static async Task DropSchemaAsync(PostgreSqlOptions options)
    {
        if (string.IsNullOrEmpty(options.ConnectionString)) return;
        await ExecuteAsync(options.ConnectionString, $"DROP SCHEMA IF EXISTS {PostgreSqlOptions.Quote(options.SchemaName)} CASCADE");
    }

    internal static async Task ExecuteAsync(string connectionString, string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task<T> ScalarAsync<T>(string connectionString, string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task WarmConnectionAsync(string connectionString)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(deadline.Token);
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync(deadline.Token);
    }

    private static bool InfrastructureRequired =>
        string.Equals(Environment.GetEnvironmentVariable("METHODCACHE_REQUIRE_INFRA_TESTS"), "true", StringComparison.OrdinalIgnoreCase);
}

[CollectionDefinition(Name)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSql";
}
