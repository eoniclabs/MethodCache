using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Providers.SqlServer.Configuration;

namespace MethodCache.Providers.SqlServer.Services;

/// <summary>
/// Default implementation of ISqlServerTableManager.
/// </summary>
public class SqlServerTableManager : ISqlServerTableManager
{
    private readonly ISqlServerConnectionManager _connectionManager;
    private readonly SqlServerOptions _options;
    private readonly ILogger<SqlServerTableManager> _logger;

    public SqlServerTableManager(
        ISqlServerConnectionManager connectionManager,
        IOptions<SqlServerOptions> options,
        ILogger<SqlServerTableManager> logger)
    {
        _connectionManager = connectionManager;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureTablesExistAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.EnableAutoTableCreation)
        {
            return;
        }

        try
        {
            // Create schema if needed
            await CreateSchemaAsync(cancellationToken);

            // Check if tables exist
            if (await TablesExistAsync(cancellationToken))
            {
                _logger.LogDebug("SQL Server cache tables already exist");
                return;
            }

            // Create tables
            await CreateEntriesTableAsync(cancellationToken);
            await CreateTagsTableAsync(cancellationToken);
            await CreateIndexesAsync(cancellationToken);

            _logger.LogInformation("Successfully created SQL Server cache tables in schema '{Schema}'", _options.Schema);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create SQL Server cache tables");
            throw;
        }
    }

    public async Task<bool> TablesExistAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _connectionManager.GetConnectionAsync(cancellationToken);

            const string checkTablesSql = @"
                SELECT COUNT(*)
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_SCHEMA = @Schema
                AND TABLE_NAME IN (@EntriesTable, @TagsTable)";

            await using var command = new SqlCommand(checkTablesSql, connection)
            {
                CommandTimeout = _options.CommandTimeoutSeconds
            };
            command.Parameters.AddWithValue("@Schema", _options.Schema);
            command.Parameters.AddWithValue("@EntriesTable", _options.EntriesTableName);
            command.Parameters.AddWithValue("@TagsTable", _options.TagsTableName);

            var tableCount = (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
            return tableCount == 2; // Both tables should exist
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if cache tables exist");
            return false;
        }
    }

    public async Task CreateSchemaAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _connectionManager.GetConnectionAsync(cancellationToken);

            // Check if schema exists
            const string checkSchemaSql = @"
                SELECT COUNT(*)
                FROM INFORMATION_SCHEMA.SCHEMATA
                WHERE SCHEMA_NAME = @Schema";

            await using var checkCommand = new SqlCommand(checkSchemaSql, connection)
            {
                CommandTimeout = _options.CommandTimeoutSeconds
            };
            checkCommand.Parameters.AddWithValue("@Schema", _options.Schema);

            var schemaExists = (int)(await checkCommand.ExecuteScalarAsync(cancellationToken) ?? 0) > 0;

            if (!schemaExists)
            {
                // Create schema
                var createSchemaSql = $"CREATE SCHEMA [{_options.Schema}]";
                await using var createCommand = new SqlCommand(createSchemaSql, connection)
                {
                    CommandTimeout = _options.CommandTimeoutSeconds
                };

                await createCommand.ExecuteNonQueryAsync(cancellationToken);
                _logger.LogInformation("Created schema '{Schema}'", _options.Schema);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating schema '{Schema}'", _options.Schema);
            throw;
        }
    }

    public async Task CreateEntriesTableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _connectionManager.GetConnectionAsync(cancellationToken);

            var createTableSql = $@"
                CREATE TABLE {_options.FullEntriesTableName} (
                    [Key] NVARCHAR(450) NOT NULL,
                    [Value] VARBINARY(MAX) NOT NULL,
                    [ExpiresAt] DATETIME2 NULL,
                    [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                    [UpdatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                    CONSTRAINT [PK_{_options.Schema}_{_options.EntriesTableName}] PRIMARY KEY ([Key])
                )";

            await using var command = new SqlCommand(createTableSql, connection)
            {
                CommandTimeout = _options.CommandTimeoutSeconds
            };

            await command.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogDebug("Created entries table {TableName}", _options.FullEntriesTableName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating entries table {TableName}", _options.FullEntriesTableName);
            throw;
        }
    }

    public async Task CreateTagsTableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _connectionManager.GetConnectionAsync(cancellationToken);

            var createTableSql = $@"
                CREATE TABLE {_options.FullTagsTableName} (
                    [Key] NVARCHAR(450) NOT NULL,
                    [Tag] NVARCHAR(200) NOT NULL,
                    CONSTRAINT [PK_{_options.Schema}_{_options.TagsTableName}] PRIMARY KEY ([Key], [Tag]),
                    CONSTRAINT [FK_{_options.Schema}_{_options.TagsTableName}_{_options.EntriesTableName}]
                        FOREIGN KEY ([Key]) REFERENCES {_options.FullEntriesTableName} ([Key])
                        ON DELETE CASCADE
                )";

            await using var command = new SqlCommand(createTableSql, connection)
            {
                CommandTimeout = _options.CommandTimeoutSeconds
            };

            await command.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogDebug("Created tags table {TableName}", _options.FullTagsTableName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating tags table {TableName}", _options.FullTagsTableName);
            throw;
        }
    }

    public async Task CreateIndexesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _connectionManager.GetConnectionAsync(cancellationToken);

            // Index on ExpiresAt for efficient cleanup
            var createExpirationIndexSql = $@"
                CREATE NONCLUSTERED INDEX [IX_{_options.Schema}_{_options.EntriesTableName}_ExpiresAt]
                ON {_options.FullEntriesTableName} ([ExpiresAt])
                WHERE [ExpiresAt] IS NOT NULL";

            await using var expirationCommand = new SqlCommand(createExpirationIndexSql, connection)
            {
                CommandTimeout = _options.CommandTimeoutSeconds
            };
            await expirationCommand.ExecuteNonQueryAsync(cancellationToken);

            // Index on Tag for efficient tag-based lookups
            var createTagIndexSql = $@"
                CREATE NONCLUSTERED INDEX [IX_{_options.Schema}_{_options.TagsTableName}_Tag]
                ON {_options.FullTagsTableName} ([Tag])
                INCLUDE ([Key])";

            await using var tagCommand = new SqlCommand(createTagIndexSql, connection)
            {
                CommandTimeout = _options.CommandTimeoutSeconds
            };
            await tagCommand.ExecuteNonQueryAsync(cancellationToken);

            // Index on CreatedAt for time-based queries
            var createCreatedAtIndexSql = $@"
                CREATE NONCLUSTERED INDEX [IX_{_options.Schema}_{_options.EntriesTableName}_CreatedAt]
                ON {_options.FullEntriesTableName} ([CreatedAt])";

            await using var createdCommand = new SqlCommand(createCreatedAtIndexSql, connection)
            {
                CommandTimeout = _options.CommandTimeoutSeconds
            };
            await createdCommand.ExecuteNonQueryAsync(cancellationToken);

            _logger.LogDebug("Created performance indexes for cache tables");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating indexes for cache tables");
            throw;
        }
    }
}