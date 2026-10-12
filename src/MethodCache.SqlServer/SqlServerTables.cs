using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MethodCache.SqlServer;

/// <summary>
/// Creates the cache and invalidations tables if they do not exist. Startup waits at most
/// <see cref="StartupWait"/> for that; an unreachable database is retried in the background, and until the tables
/// exist, methods run uncached.
/// </summary>
internal sealed class SqlServerTables(SqlServerOptions options, ILogger<SqlServerTables>? logger = null) : IHostedService, IDisposable
{
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _stopping = new();
    private Task? _creating;

    internal Task? Creating => _creating;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Normally the tables are ready before the first request; a database that is down only delays startup a bit
        _creating = CreateWithRetriesAsync(_stopping.Token);
        try
        {
            await _creating.WaitAsync(StartupWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_creating != null)
        {
            await _creating.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        // Disposed without StopAsync, e.g. when startup was cancelled: the retries stop rather than outlive the app
        _stopping.Cancel();
        _stopping.Dispose();
    }

    private async Task CreateWithRetriesAsync(CancellationToken cancellationToken)
    {
        var logged = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await CreateIfMissingAsync(options, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!logged)
                {
                    logged = true;
                    logger?.LogWarning(ex, "MethodCache could not create its SQL Server tables; retrying every {Interval}.", RetryInterval);
                }
            }

            try
            {
                await Task.Delay(RetryInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

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
                    [Scope] binary(32) NOT NULL,
                    [InstanceId] varchar(32) NOT NULL,
                    [MessageType] tinyint NOT NULL,
                    [Target] nvarchar(max) NOT NULL,
                    [CreatedAt] datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
            END

            -- Earlier versions did not isolate messages by prefix. Preserve those rows, but leave their
            -- scope NULL so no application treats an ambiguous legacy message as its own invalidation.
            IF COL_LENGTH(@invalidations, 'Scope') IS NULL
                ALTER TABLE {options.QualifiedInvalidationsTableName} ADD [Scope] binary(32) NULL;

            -- Dynamic SQL compiles after the optional ALTER, including on an existing old table.
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(@invalidations) AND name = 'Index_Scope_Id')
                EXEC(N'CREATE NONCLUSTERED INDEX [Index_Scope_Id] ON {options.QualifiedInvalidationsTableName.Replace("'", "''", StringComparison.Ordinal)} ([Scope], [Id]);');
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(@invalidations) AND name = 'Index_Scope_CreatedAt')
                EXEC(N'CREATE NONCLUSTERED INDEX [Index_Scope_CreatedAt] ON {options.QualifiedInvalidationsTableName.Replace("'", "''", StringComparison.Ordinal)} ([Scope], [CreatedAt]);');
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
