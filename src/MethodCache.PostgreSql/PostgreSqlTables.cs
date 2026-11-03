using MethodCache.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MethodCache.PostgreSql;

/// <summary>
/// Creates the cache tables if they do not exist, and deletes expired entries now and then. Startup waits at most
/// <see cref="StartupWait"/> for the tables; an unreachable database is retried in the background, and until the
/// tables exist, methods run uncached.
/// </summary>
internal sealed class PostgreSqlTables(
    PostgreSqlConnection connection,
    PostgreSqlOptions options,
    ILogger<PostgreSqlTables>? logger = null,
    Func<PostgreSqlOptions, CancellationToken, Task>? createTables = null,
    TimeProvider? timeProvider = null) : IHostedService, IDisposable
{
    internal const int SchemaVersion = 1;
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(3);

    /// <summary>How often each instance deletes expired entries, plus up to a fifth of it at random.</summary>
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    internal const int SweepBatchSize = 1000;
    internal const int MaxSweepBatches = 100;

    // One lock for every MethodCache schema in the database: two configurations sharing a schema can't race
    // to create it. Transaction-scoped, so it also works behind a transaction-pooling PgBouncer.
    private const long SchemaLockKey = 0x4D43_5047_5343_4831; // "MCPGSCH1"

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _creating;
    private Task? _sweeping;

    internal Task? Creating => _creating;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // On a thread-pool thread: opening a connection can block before its first await, and that time would not
        // count against StartupWait. The token is read here: Dispose may run before the work starts.
        var stopping = _stopping.Token;
        _creating = Task.Run(() => CreateWithRetriesAsync(stopping), CancellationToken.None);
        _sweeping = Task.Run(() => SweepAsync(stopping), CancellationToken.None);
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
        foreach (var task in new[] { _creating, _sweeping })
        {
            if (task == null) continue;
            try { await task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }
            // Logged when it happened; the host stops either way
            catch (IncompatibleDistributedScopeException) { }
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
                await (createTables ?? ((o, token) => CreateIfMissingAsync(connection.DataSource, o, token)))(options, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IncompatibleDistributedScopeException ex)
            {
                // Report errors even when initialization finishes after the bounded startup wait.
                logger?.LogError("MethodCache's PostgreSQL schema is incompatible; table initialization stopped. {Reason}", ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                if (!logged)
                {
                    logged = true;
                    logger?.LogWarning("MethodCache could not create its PostgreSQL tables; retrying every {Interval}. Error type: {ErrorType}.", RetryInterval, ex.GetType().FullName);
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

    /// <summary>
    /// Every instance deletes expired entries in small batches. <c>SKIP LOCKED</c> lets instances that sweep at the
    /// same time share the rows instead of waiting for each other, so no instance has to be chosen to do it.
    /// </summary>
    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        var sql = $"""
            DELETE FROM {options.QualifiedTableName} WHERE id IN (
                SELECT id FROM {options.QualifiedTableName} WHERE expires_at < now()
                LIMIT {SweepBatchSize} FOR UPDATE SKIP LOCKED)
            """;
        var logged = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var jitter = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * SweepInterval.TotalMilliseconds / 5);
            try
            {
                await Task.Delay(SweepInterval + jitter, _timeProvider, cancellationToken).ConfigureAwait(false);
                if (_creating is not { IsCompletedSuccessfully: true }) continue;
                await SweepOnceAsync(connection.DataSource, sql, cancellationToken).ConfigureAwait(false);
                logged = false;
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
                    logger?.LogWarning("MethodCache could not delete expired entries from PostgreSQL; it tries again later. Error type: {ErrorType}.", ex.GetType().FullName);
                }
            }
        }
    }

    internal static async Task<int> SweepOnceAsync(NpgsqlDataSource dataSource, string sql, CancellationToken cancellationToken)
    {
        var deleted = 0;
        for (var batch = 0; batch < MaxSweepBatches; batch++)
        {
            using var command = dataSource.CreateCommand(sql);
            var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            deleted += rows;
            if (rows < SweepBatchSize) break;
        }

        return deleted;
    }

    /// <summary>
    /// Creates the schema and the four tables when they are missing, or checks the ones that exist. Throws
    /// <see cref="IncompatibleDistributedScopeException"/> when they don't match schema version 1.
    /// </summary>
    internal static async Task CreateIfMissingAsync(NpgsqlDataSource dataSource, PostgreSqlOptions options, CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                await ExecuteAsync(connection, "SET LOCAL lock_timeout = '3s'", cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, $"SELECT pg_advisory_xact_lock({SchemaLockKey})", cancellationToken).ConfigureAwait(false);
                await CreateOrCheckAsync(connection, options, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task CreateOrCheckAsync(NpgsqlConnection connection, PostgreSqlOptions options, CancellationToken cancellationToken)
    {
        var create = options.CreateTablesIfMissing;
        var tables = new[] { options.QualifiedVersionTableName, options.QualifiedTableName, options.QualifiedInvalidationsTableName, options.QualifiedCursorTableName };
        var exists = new bool[tables.Length];
        bool schemaExists;
        using (var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = $1), to_regclass($2) IS NOT NULL, to_regclass($3) IS NOT NULL, to_regclass($4) IS NOT NULL, to_regclass($5) IS NOT NULL",
            connection))
        {
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = options.SchemaName });
            foreach (var table in tables) command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = table });
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                schemaExists = reader.GetBoolean(0);
                for (var i = 0; i < tables.Length; i++) exists[i] = reader.GetBoolean(i + 1);
            }
        }

        if (!schemaExists)
        {
            if (!create) throw Incompatible("The MethodCache schema is missing. Apply the migration script in the package's migrations folder.");
            await ExecuteAsync(connection, $"CREATE SCHEMA IF NOT EXISTS {PostgreSqlOptions.Quote(options.SchemaName)}", cancellationToken).ConfigureAwait(false);
        }

        if (!exists[0])
        {
            if (exists[1] || exists[2] || exists[3])
                throw Incompatible("The MethodCache tables have no schema version. Apply a supported migration before starting this version.");
            if (!create) throw Incompatible("The MethodCache tables are missing. Apply the migration script in the package's migrations folder.");
            await ExecuteAsync(connection, Script(options), cancellationToken).ConfigureAwait(false);
            await TuneColumnsAsync(connection, options, cancellationToken).ConfigureAwait(false);
            return;
        }

        await CheckVersionAsync(connection, options, cancellationToken).ConfigureAwait(false);
        if (!create && (!exists[1] || !exists[2] || !exists[3]))
            throw Incompatible("The MethodCache schema is incomplete. Apply the migration script in the package's migrations folder.");
        if (!exists[1] || !exists[2] || !exists[3])
            await ExecuteAsync(connection, Script(options, versionTable: false), cancellationToken).ConfigureAwait(false);
        if (!exists[1]) await TuneColumnsAsync(connection, options, cancellationToken).ConfigureAwait(false);

        await CheckColumnsAsync(connection, options.QualifiedTableName, ["id:bytea", "value:bytea", "expires_at:timestamp with time zone"], ["id"], cancellationToken).ConfigureAwait(false);
        await CheckColumnsAsync(connection, options.QualifiedInvalidationsTableName,
            ["scope:bytea", "id:bigint", "instance_id:text", "message_type:smallint", "target:text", "created_at:timestamp with time zone"], ["scope", "id"], cancellationToken).ConfigureAwait(false);
        await CheckColumnsAsync(connection, options.QualifiedCursorTableName, ["scope:bytea", "last_id:bigint", "retention:interval"], ["scope"], cancellationToken).ConfigureAwait(false);
        await CheckIndexesAsync(connection, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The tables of schema version 1. The migration script in the package is this text for the default names.
    /// </summary>
    internal static string Script(PostgreSqlOptions options, bool versionTable = true)
    {
        var expiresIndex = PostgreSqlOptions.Quote(options.TableName + PostgreSqlOptions.ExpiresIndexSuffix);
        var createdIndex = PostgreSqlOptions.Quote(options.InvalidationsTableName + PostgreSqlOptions.CreatedIndexSuffix);
        var version = versionTable ? $"""
            CREATE TABLE {options.QualifiedVersionTableName} (
                id smallint NOT NULL PRIMARY KEY CHECK (id = 1),
                version integer NOT NULL);
            INSERT INTO {options.QualifiedVersionTableName} (id, version) VALUES (1, {SchemaVersion});

            """ : "";
        return version + $"""
            CREATE TABLE IF NOT EXISTS {options.QualifiedTableName} (
                id bytea NOT NULL PRIMARY KEY,
                value bytea NOT NULL,
                expires_at timestamptz NOT NULL);
            CREATE INDEX IF NOT EXISTS {expiresIndex} ON {options.QualifiedTableName} (expires_at);

            CREATE TABLE IF NOT EXISTS {options.QualifiedInvalidationsTableName} (
                scope bytea NOT NULL,
                id bigint NOT NULL,
                instance_id text NOT NULL,
                message_type smallint NOT NULL,
                target text NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (scope, id));
            CREATE INDEX IF NOT EXISTS {createdIndex} ON {options.QualifiedInvalidationsTableName} (scope, created_at);

            CREATE TABLE IF NOT EXISTS {options.QualifiedCursorTableName} (
                scope bytea NOT NULL PRIMARY KEY,
                last_id bigint NOT NULL,
                retention interval NOT NULL DEFAULT '0');
            """;
    }

    /// <summary>
    /// Tunes the columns of a new cache table. <c>STORAGE MAIN</c> keeps values up to about 8 KB in the row, so a read
    /// needs no second lookup in the TOAST table; values are still compressed from about 2 KB, and only larger ones
    /// move out of the row. The 32-byte id is <c>PLAIN</c>: otherwise PostgreSQL would move it out of the row first,
    /// as the only other candidate. lz4, where the server supports it (PostgreSQL 14 and later, built with lz4),
    /// compresses faster than the default pglz.
    /// </summary>
    private static async Task TuneColumnsAsync(NpgsqlConnection connection, PostgreSqlOptions options, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection,
            $"ALTER TABLE {options.QualifiedTableName} ALTER COLUMN id SET STORAGE PLAIN, ALTER COLUMN value SET STORAGE MAIN",
            cancellationToken).ConfigureAwait(false);
        using var check = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_settings WHERE name = 'default_toast_compression' AND 'lz4' = ANY(enumvals))", connection);
        if (await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            await ExecuteAsync(connection, $"ALTER TABLE {options.QualifiedTableName} ALTER COLUMN value SET COMPRESSION lz4", cancellationToken).ConfigureAwait(false);
    }

    private static async Task CheckVersionAsync(NpgsqlConnection connection, PostgreSqlOptions options, CancellationToken cancellationToken)
    {
        await CheckColumnsAsync(connection, options.QualifiedVersionTableName, ["id:smallint", "version:integer"], ["id"], cancellationToken).ConfigureAwait(false);
        using var command = new NpgsqlCommand($"SELECT count(*) = 1 AND bool_and(id = 1 AND version = {SchemaVersion}) FROM {options.QualifiedVersionTableName}", connection);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw Incompatible("The MethodCache schema version is not supported. Apply a supported migration before starting.");
    }

    private static async Task CheckColumnsAsync(NpgsqlConnection connection, string table, string[] expected, string[] primaryKey, CancellationToken cancellationToken)
    {
        using var command = new NpgsqlCommand("""
            SELECT coalesce(array_agg(attname || ':' || format_type(atttypid, atttypmod) ORDER BY attname) FILTER (WHERE attnotnull), '{}'),
                   count(*) FILTER (WHERE NOT attnotnull),
                   (SELECT coalesce(array_agg(a.attname ORDER BY k.ordinal), '{}')
                    FROM pg_index i
                    CROSS JOIN unnest(i.indkey) WITH ORDINALITY AS k(attnum, ordinal)
                    JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum
                    WHERE i.indrelid = $1::regclass AND i.indisprimary)
            FROM pg_attribute WHERE attrelid = $1::regclass AND attnum > 0 AND NOT attisdropped
            """, connection);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = table });
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var columns = reader.GetFieldValue<string[]>(0);
            var nullable = reader.GetInt64(1);
            var key = reader.GetFieldValue<string[]>(2);
            if (nullable != 0 || !columns.SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
                !key.SequenceEqual(primaryKey, StringComparer.Ordinal))
                throw Incompatible($"The columns of {table} are incompatible with MethodCache schema version {SchemaVersion}. Apply a supported migration.");
        }
    }

    private static async Task CheckIndexesAsync(NpgsqlConnection connection, PostgreSqlOptions options, CancellationToken cancellationToken)
    {
        using var command = new NpgsqlCommand(
            "SELECT to_regclass($1) IS NOT NULL AND to_regclass($2) IS NOT NULL", connection);
        var schema = PostgreSqlOptions.Quote(options.SchemaName) + ".";
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = schema + PostgreSqlOptions.Quote(options.TableName + PostgreSqlOptions.ExpiresIndexSuffix) });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = schema + PostgreSqlOptions.Quote(options.InvalidationsTableName + PostgreSqlOptions.CreatedIndexSuffix) });
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw Incompatible("The MethodCache indexes are missing. Apply the migration script in the package's migrations folder.");
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IncompatibleDistributedScopeException Incompatible(string message) => new(message);
}
