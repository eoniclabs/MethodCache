using Microsoft.Data.SqlClient;

namespace MethodCache.Providers.SqlServer.Services;

/// <summary>
/// Manages SQL Server database connections for cache operations.
/// </summary>
public interface ISqlServerConnectionManager
{
    /// <summary>
    /// Gets an open SQL Server connection.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An open SqlConnection instance.</returns>
    Task<SqlConnection> GetConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests the connection to ensure it's working properly.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if connection is healthy, false otherwise.</returns>
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
}