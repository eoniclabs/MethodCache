using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Providers.SqlServer.Configuration;

namespace MethodCache.Providers.SqlServer.Services;

/// <summary>
/// Default implementation of ISqlServerConnectionManager.
/// </summary>
public class SqlServerConnectionManager : ISqlServerConnectionManager
{
    private readonly SqlServerOptions _options;
    private readonly ILogger<SqlServerConnectionManager> _logger;

    public SqlServerConnectionManager(
        IOptions<SqlServerOptions> options,
        ILogger<SqlServerConnectionManager> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SqlConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(_options.ConnectionString);

        try
        {
            // Connection timeout is set via connection string

            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await GetConnectionAsync(cancellationToken);

            const string testSql = "SELECT 1";
            await using var command = new SqlCommand(testSql, connection)
            {
                CommandTimeout = 5 // Short timeout for health checks
            };

            await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SQL Server connection test failed");
            return false;
        }
    }
}