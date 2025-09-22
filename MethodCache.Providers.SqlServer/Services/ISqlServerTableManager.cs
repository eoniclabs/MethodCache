namespace MethodCache.Providers.SqlServer.Services;

/// <summary>
/// Manages database table creation and schema management for SQL Server cache storage.
/// </summary>
public interface ISqlServerTableManager
{
    /// <summary>
    /// Ensures that all required cache tables exist in the database.
    /// Creates tables if they don't exist and auto-creation is enabled.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task EnsureTablesExistAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the required cache tables exist in the database.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if all tables exist, false otherwise.</returns>
    Task<bool> TablesExistAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the cache schema if it doesn't exist.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CreateSchemaAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the cache entries table.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CreateEntriesTableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the cache tags table.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CreateTagsTableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates all necessary indexes for optimal performance.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CreateIndexesAsync(CancellationToken cancellationToken = default);
}