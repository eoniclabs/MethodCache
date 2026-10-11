using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;

namespace MethodCache.SqlServer;

/// <summary>
/// Creates the cache and invalidations tables at startup if they do not exist.
/// </summary>
internal sealed class SqlServerTables(SqlServerOptions options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => CreateIfMissingAsync(options, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static async Task CreateIfMissingAsync(SqlServerOptions options, CancellationToken cancellationToken)
    {
        // The cache table matches what `dotnet sql-cache create` makes for Microsoft.Extensions.Caching.SqlServer
        var sql = $"""
            IF SCHEMA_ID(@schema) IS NULL
                EXEC(N'CREATE SCHEMA ' + @quotedSchema);

            IF OBJECT_ID(@table, N'U') IS NULL
            BEGIN
                CREATE TABLE {options.QualifiedTableName} (
                    [Id] nvarchar(449) COLLATE SQL_Latin1_General_CP1_CS_AS NOT NULL PRIMARY KEY,
                    [Value] varbinary(max) NOT NULL,
                    [ExpiresAtTime] datetimeoffset NOT NULL,
                    [SlidingExpirationInSeconds] bigint NULL,
                    [AbsoluteExpiration] datetimeoffset NULL);
                CREATE NONCLUSTERED INDEX [Index_ExpiresAtTime] ON {options.QualifiedTableName} ([ExpiresAtTime]);
            END

            IF OBJECT_ID(@invalidations, N'U') IS NULL
            BEGIN
                CREATE TABLE {options.QualifiedInvalidationsTableName} (
                    [Id] bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [InstanceId] varchar(32) NOT NULL,
                    [MessageType] tinyint NOT NULL,
                    [Target] nvarchar(max) NOT NULL,
                    [CreatedAt] datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
                CREATE NONCLUSTERED INDEX [Index_CreatedAt] ON {options.QualifiedInvalidationsTableName} ([CreatedAt]);
            END
            """;

        var connection = new SqlConnection(options.ConnectionString);
        await using var connectionScope = connection.ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = new SqlCommand(sql, connection);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(new SqlParameter("@schema", System.Data.SqlDbType.NVarChar, 128) { Value = options.SchemaName });
        command.Parameters.Add(new SqlParameter("@quotedSchema", System.Data.SqlDbType.NVarChar, 260) { Value = SqlServerOptions.Quote(options.SchemaName) });
        command.Parameters.Add(new SqlParameter("@table", System.Data.SqlDbType.NVarChar, 520) { Value = options.QualifiedTableName });
        command.Parameters.Add(new SqlParameter("@invalidations", System.Data.SqlDbType.NVarChar, 520) { Value = options.QualifiedInvalidationsTableName });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
