using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MethodCache.SqlServer;

/// <summary>
/// Creates the cache and invalidations tables if they do not exist. Startup waits at most
/// <see cref="StartupWait"/> for that; an unreachable database is retried in the background, and until the tables
/// exist, methods run uncached.
/// </summary>
internal sealed class SqlServerTables(
    SqlServerOptions options,
    ILogger<SqlServerTables>? logger = null,
    Func<SqlServerOptions, CancellationToken, Task>? createTables = null,
    TimeProvider? timeProvider = null) : IHostedService, IDisposable
{
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _creating;

    internal Task? Creating => _creating;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Normally the tables are ready before the first request; a database that is down only delays startup a bit
        // On a thread-pool thread: SqlClient can block before its first await (e.g. while connecting), and that
        // time would not count against StartupWait. The token is read here: Dispose may run before the work starts.
        var stopping = _stopping.Token;
        _creating = Task.Run(() => CreateWithRetriesAsync(stopping), CancellationToken.None);
        try
        {
            await _creating.WaitAsync(StartupWait, _timeProvider, cancellationToken).ConfigureAwait(false);
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
                await (createTables ?? CreateIfMissingAsync)(options, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SqlException ex) when (ex.Number == 50001)
            {
                // Report errors even when initialization finishes after the bounded startup wait.
                logger?.LogError(ex, "MethodCache SQL Server schema is incompatible; table initialization stopped. Apply the explicit MethodCache migration script.");
                throw;
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
                await Task.Delay(RetryInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
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
            SET XACT_ABORT ON;
            BEGIN TRY
                BEGIN TRANSACTION;
                DECLARE @locked int;
                EXEC @locked = sys.sp_getapplock @Resource = @schemaLock, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 3000;
                IF @locked < 0 THROW 50002, 'MethodCache could not acquire its schema initialization lock.', 1;
                IF SCHEMA_ID(@schema) IS NULL
                BEGIN
                    IF @create = 0 THROW 50001, 'MethodCache schema is missing. Apply the schema migration script.', 1;
                    EXEC(N'CREATE SCHEMA ' + @quotedSchema);
                END
                IF OBJECT_ID(@versionTable, N'U') IS NULL
                BEGIN
                    IF OBJECT_ID(@table, N'U') IS NOT NULL OR OBJECT_ID(@invalidations, N'U') IS NOT NULL OR @create = 0
                        THROW 50001, 'MethodCache schema is unversioned. Apply the explicit migration script before starting this version.', 1;
                    CREATE TABLE {options.QualifiedVersionTableName} ([Id] tinyint NOT NULL PRIMARY KEY CHECK ([Id] = 1), [Version] int NOT NULL);
                    INSERT INTO {options.QualifiedVersionTableName} ([Id], [Version]) VALUES (1, 1);
                END
                IF EXISTS (
                    SELECT 1 FROM (VALUES
                (N'Id', TYPE_ID('tinyint'), 1, 0, 0),
                (N'Version', TYPE_ID('int'), 4, 0, 0)
                    ) AS expected([Name], [Type], [Length], [Nullable], [Identity])
                    LEFT JOIN sys.columns actual ON actual.object_id = OBJECT_ID(@versionTable) AND actual.name = expected.[Name]
                    WHERE actual.column_id IS NULL OR actual.system_type_id <> expected.[Type]
                        OR actual.max_length <> expected.[Length] OR actual.is_nullable <> expected.[Nullable]
                        OR actual.is_identity <> expected.[Identity])
                    THROW 50001, 'MethodCache schema-version columns are incompatible. Apply a supported explicit migration.', 1;
                EXEC(N'IF (SELECT COUNT(*) FROM ' + @versionTable + N') <> 1 OR NOT EXISTS (SELECT 1 FROM '
                    + @versionTable + N' WHERE [Id] = 1 AND [Version] = 1) THROW 50001, ''MethodCache SQL schema version is incompatible. Apply a supported migration before starting.'', 1;');
                IF @create = 0 AND (OBJECT_ID(@table, N'U') IS NULL OR OBJECT_ID(@invalidations, N'U') IS NULL)
                    THROW 50001, 'MethodCache schema is incomplete. Apply the schema migration script.', 1;

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

            IF NOT EXISTS (
                SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(@invalidations) AND name = 'Scope'
                    AND system_type_id = TYPE_ID('binary') AND max_length = 32 AND is_nullable = 0)
                THROW 50001, 'MethodCache invalidations table must contain a non-nullable binary(32) Scope column. Apply a supported explicit migration.', 1;

            IF EXISTS (
                SELECT 1 FROM (VALUES
            (N'Id', TYPE_ID('nvarchar'), 898, 0, 0),
            (N'Value', TYPE_ID('varbinary'), -1, 0, 0),
            (N'ExpiresAtTime', TYPE_ID('datetimeoffset'), 10, 0, 0),
            (N'SlidingExpirationInSeconds', TYPE_ID('bigint'), 8, 1, 0),
            (N'AbsoluteExpiration', TYPE_ID('datetimeoffset'), 10, 1, 0)
                ) AS expected([Name], [Type], [Length], [Nullable], [Identity])
                LEFT JOIN sys.columns actual ON actual.object_id = OBJECT_ID(@table) AND actual.name = expected.[Name]
                WHERE actual.column_id IS NULL OR actual.system_type_id <> expected.[Type]
                    OR actual.max_length <> expected.[Length] OR actual.is_nullable <> expected.[Nullable]
                    OR actual.is_identity <> expected.[Identity])
                THROW 50001, 'MethodCache cache columns are incompatible. Apply a supported explicit migration.', 1;
            IF EXISTS (
                SELECT 1 FROM (VALUES
            (N'Id', TYPE_ID('bigint'), 8, 0, 1),
            (N'Scope', TYPE_ID('binary'), 32, 0, 0),
            (N'InstanceId', TYPE_ID('varchar'), 32, 0, 0),
            (N'MessageType', TYPE_ID('tinyint'), 1, 0, 0),
            (N'Target', TYPE_ID('nvarchar'), -1, 0, 0),
            (N'CreatedAt', TYPE_ID('datetime2'), 8, 0, 0)
                ) AS expected([Name], [Type], [Length], [Nullable], [Identity])
                LEFT JOIN sys.columns actual ON actual.object_id = OBJECT_ID(@invalidations) AND actual.name = expected.[Name]
                WHERE actual.column_id IS NULL OR actual.system_type_id <> expected.[Type]
                    OR actual.max_length <> expected.[Length] OR actual.is_nullable <> expected.[Nullable]
                    OR actual.is_identity <> expected.[Identity])
                THROW 50001, 'MethodCache invalidations columns are incompatible. Apply a supported explicit migration.', 1;

            -- Compile index creation only after validating the columns of an existing table.
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(@invalidations) AND name = 'Index_Scope_Id')
            BEGIN
                IF @create = 0 THROW 50001, 'MethodCache backplane indexes are missing. Apply the schema migration script.', 1;
                EXEC(N'CREATE NONCLUSTERED INDEX [Index_Scope_Id] ON {options.QualifiedInvalidationsTableName.Replace("'", "''", StringComparison.Ordinal)} ([Scope], [Id]);');
            END
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(@invalidations) AND name = 'Index_Scope_CreatedAt')
            BEGIN
                IF @create = 0 THROW 50001, 'MethodCache backplane indexes are missing. Apply the schema migration script.', 1;
                EXEC(N'CREATE NONCLUSTERED INDEX [Index_Scope_CreatedAt] ON {options.QualifiedInvalidationsTableName.Replace("'", "''", StringComparison.Ordinal)} ([Scope], [CreatedAt]);');
            END
                COMMIT TRANSACTION;
            END TRY
            BEGIN CATCH IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION; THROW; END CATCH;
            """;

        var connection = new SqlConnection(options.ConnectionString);
        await using var connectionScope = connection.ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = new SqlCommand(sql, connection);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(new SqlParameter("@create", System.Data.SqlDbType.Bit) { Value = options.CreateTablesIfMissing });
        command.Parameters.Add(new SqlParameter("@versionTable", System.Data.SqlDbType.NVarChar, 520) { Value = options.QualifiedVersionTableName });
        command.Parameters.Add(new SqlParameter("@schemaLock", System.Data.SqlDbType.NVarChar, 255)
        { Value = "MethodCacheSchema:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(options.QualifiedTableName + "|" + options.QualifiedInvalidationsTableName))) });
        command.Parameters.Add(new SqlParameter("@schema", System.Data.SqlDbType.NVarChar, 128) { Value = options.SchemaName });
        command.Parameters.Add(new SqlParameter("@quotedSchema", System.Data.SqlDbType.NVarChar, 260) { Value = SqlServerOptions.Quote(options.SchemaName) });
        command.Parameters.Add(new SqlParameter("@table", System.Data.SqlDbType.NVarChar, 520) { Value = options.QualifiedTableName });
        command.Parameters.Add(new SqlParameter("@invalidations", System.Data.SqlDbType.NVarChar, 520) { Value = options.QualifiedInvalidationsTableName });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
