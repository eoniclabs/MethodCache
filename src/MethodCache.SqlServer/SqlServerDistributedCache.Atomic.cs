using System.Buffers.Binary;
using System.Data;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Data.SqlClient;

namespace MethodCache.SqlServer;

internal sealed partial class SqlServerDistributedCache
{
    // Only the latest bounded batch shape is retained, never a key or generation value.
    private sealed record EpochCommand(int Count, string Text);
    private EpochCommand? _epochCommand;

    // The stamp of a key that was never invalidated: long.MinValue, little-endian.
    private const string EmptyStamp = "0x0000000000000080";

    // @data contains stamp + version + init-id + BE revision. Used inside the caller's serializable key lock.
    private const string AdvanceEpochSql = """
        DECLARE @high bigint = CONVERT(bigint, SUBSTRING(@data, 26, 4));
        DECLARE @low bigint = CONVERT(bigint, SUBSTRING(@data, 30, 4));
        DECLARE @id varbinary(16) = SUBSTRING(@data, 10, 16);
        IF @low = 4294967295
        BEGIN
            SET @low = 0;
            IF @high = 4294967295 BEGIN SET @high = 0; SET @id = @seed; END
            ELSE SET @high = @high + 1;
        END
        ELSE SET @low = @low + 1;
        SET @data = SUBSTRING(@data, 1, 9) + @id + CONVERT(binary(4), @high) + CONVERT(binary(4), @low) + SUBSTRING(@data, 34, 2147483647);
        """;

    public async ValueTask<DistributedRead> ReadEntryAsync(string valueKey, string removalKey, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var valueId = GetPhysicalKey(valueKey);
        var removalId = GetPhysicalKey(removalKey);
        var connection = new SqlConnection(_connectionString);
        await using var connectionScope = connection.ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = new SqlCommand($"SELECT [Id], [Value] FROM {_table} WHERE [Id] IN (@value, @removal) AND [ExpiresAtTime] > SYSDATETIMEOFFSET();", connection);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(new SqlParameter("@value", SqlDbType.NVarChar, 449) { Value = valueId });
        command.Parameters.Add(new SqlParameter("@removal", SqlDbType.NVarChar, 449) { Value = removalId });
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        byte[]? value = null;
        byte[]? removal = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetString(0) == valueId) value = (byte[])reader.GetValue(1);
            else removal = (byte[])reader.GetValue(1);
        }
        return new DistributedRead(value, removal);
    }

    public async ValueTask<CacheEpoch[]> EnsureEpochsAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();
        if (keys.Count > 128) throw new ArgumentOutOfRangeException(nameof(keys), "A metadata batch may contain at most 128 keys.");
        if (keys.Count == 0) return [];
        foreach (var key in keys) ArgumentNullException.ThrowIfNull(key);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        // A single command/round trip. Sorting physical IDs gives concurrent vectors one lock order.
        var ids = new string[keys.Count];
        var positions = new int[keys.Count];
        for (var i = 0; i < ids.Length; i++) (ids[i], positions[i]) = (GetPhysicalKey(keys[i]), i);
        Array.Sort(ids, positions, StringComparer.Ordinal);
        var connection = new SqlConnection(_connectionString);
        await using var connectionScope = connection.ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = new SqlCommand(EpochCommandText(ids.Length), connection);
        await using var commandScope = command.ConfigureAwait(false);
        AddEpochParameters(command);
        for (var i = 0; i < ids.Length; i++)
            command.Parameters.Add(new SqlParameter("@key" + i, SqlDbType.NVarChar, 449) { Value = ids[i] });
        var result = new CacheEpoch[keys.Count];
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        // The first result holds the rows that are already initialized and permanent. Only when one is missing does
        // the command lock the vector and return every position again, one result per key.
        var found = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { ReadEpoch(reader, positions, result); found++; }
        if (found == ids.Length) return result;
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ReadEpoch(reader, positions, result);
        return result;
    }

    private static void ReadEpoch(SqlDataReader reader, int[] positions, CacheEpoch[] result)
    {
        if (!EpochMetadata.TryRead((byte[])reader.GetValue(1), out result[positions[reader.GetInt32(0)]]))
            throw new IncompatibleDistributedScopeException("The distributed epoch metadata has an incompatible schema.");
    }

    // The shape uses sorted positions only; caller positions are restored after the query. Reading an initialized,
    // permanent vector takes no update locks, so concurrent readers of the same URL never queue on each other.
    internal string EpochCommandText(int count)
    {
        if (count is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(count));
        var cached = Volatile.Read(ref _epochCommand);
        if (cached?.Count == count) return cached.Text;
        var sql = new System.Text.StringBuilder("SELECT k.[Position], t.[Value] FROM (VALUES ");
        for (var i = 0; i < count; i++) sql.Append(i == 0 ? "" : ", ").Append($"({i}, @key{i})");
        sql.Append($"""
            ) AS k ([Position], [Id]) JOIN {_table} AS t ON t.[Id] = k.[Id]
                WHERE t.[ExpiresAtTime] = @maximum AND t.[AbsoluteExpiration] = @maximum AND t.[SlidingExpirationInSeconds] IS NULL
                AND DATALENGTH(t.[Value]) >= 33 AND SUBSTRING(t.[Value], 9, 1) = 0x01;
            IF @@ROWCOUNT = {count} RETURN;

            """);
        sql.Append("SET XACT_ABORT ON; BEGIN TRY BEGIN TRANSACTION; DECLARE @previous varbinary(max), @data varbinary(max), @expires datetimeoffset, @absolute datetimeoffset, @sliding bigint;\n");
        for (var i = 0; i < count; i++)
        {
            sql.Append($"""
                SET @previous = NULL; SET @expires = NULL; SET @absolute = NULL; SET @sliding = NULL;
                SELECT @previous = [Value], @expires = [ExpiresAtTime], @absolute = [AbsoluteExpiration], @sliding = [SlidingExpirationInSeconds]
                    FROM {_table} WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = @key{i};
                IF @expires <= SYSDATETIMEOFFSET() SET @previous = NULL;
                IF @previous IS NULL OR DATALENGTH(@previous) = 8
                    SET @data = ISNULL(@previous, {EmptyStamp}) + 0x01 + CONVERT(binary(16), NEWID()) + 0x0000000000000000;
                ELSE IF DATALENGTH(@previous) >= 33 AND SUBSTRING(@previous, 9, 1) = 0x01 SET @data = @previous;
                ELSE THROW 50001, 'MethodCache epoch metadata schema mismatch.', 1;
                IF @expires IS NULL
                    INSERT INTO {_table} ([Id], [Value], [ExpiresAtTime], [AbsoluteExpiration], [SlidingExpirationInSeconds]) VALUES (@key{i}, @data, @maximum, @maximum, NULL);
                ELSE IF @previous IS NULL OR @data <> @previous OR @expires <> @maximum
                    OR @absolute IS NULL OR @absolute <> @maximum OR @sliding IS NOT NULL
                    UPDATE {_table} SET [Value] = @data, [ExpiresAtTime] = @maximum, [AbsoluteExpiration] = @maximum, [SlidingExpirationInSeconds] = NULL WHERE [Id] = @key{i};
                SELECT {i} AS [Position], @data AS [Value];
                """);
        }
        sql.Append("COMMIT TRANSACTION; END TRY BEGIN CATCH IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION; THROW; END CATCH;");
        var command = new EpochCommand(count, sql.ToString());
        Volatile.Write(ref _epochCommand, command);
        return command.Text;
    }

    public async ValueTask<CacheEpoch> AdvanceEpochAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var sql = $"""
            SET XACT_ABORT ON;
            BEGIN TRY
                BEGIN TRANSACTION;
                DECLARE @previous varbinary(max), @data varbinary(max), @expires datetimeoffset;
                SELECT @previous = [Value], @expires = [ExpiresAtTime] FROM {_table} WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = @key;
                IF @expires <= SYSDATETIMEOFFSET() SET @previous = NULL;
                IF @previous IS NULL OR DATALENGTH(@previous) = 8
                    SET @data = ISNULL(@previous, {EmptyStamp}) + 0x01 + @seed + 0x0000000000000000;
                ELSE IF DATALENGTH(@previous) >= 33 AND SUBSTRING(@previous, 9, 1) = 0x01 SET @data = @previous;
                ELSE THROW 50001, 'MethodCache epoch metadata schema mismatch.', 1;
                {AdvanceEpochSql}
                IF @expires IS NULL
                    INSERT INTO {_table} ([Id], [Value], [ExpiresAtTime], [AbsoluteExpiration], [SlidingExpirationInSeconds]) VALUES (@key, @data, @maximum, @maximum, NULL);
                ELSE UPDATE {_table} SET [Value] = @data, [ExpiresAtTime] = @maximum, [AbsoluteExpiration] = @maximum, [SlidingExpirationInSeconds] = NULL WHERE [Id] = @key;
                COMMIT TRANSACTION;
                SELECT @data;
            END TRY
            BEGIN CATCH IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION; THROW; END CATCH;
            """;
        var connection = new SqlConnection(_connectionString);
        await using var connectionScope = connection.ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = new SqlCommand(sql, connection);
        await using var commandScope = command.ConfigureAwait(false);
        AddEpochParameters(command);
        command.Parameters.Add(new SqlParameter("@key", SqlDbType.NVarChar, 449) { Value = GetPhysicalKey(key) });
        command.Parameters.Add(new SqlParameter("@seed", SqlDbType.VarBinary, 16) { Value = Guid.NewGuid().ToByteArray() });
        var bytes = (byte[])(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        if (!EpochMetadata.TryRead(bytes, out var result)) throw new IncompatibleDistributedScopeException("The distributed epoch metadata has an incompatible schema.");
        return result;
    }

    public async ValueTask<TimeSpan> EnsureRetentionContractAsync(string key, TimeSpan requested, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (requested <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requested));
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var sql = $"""
            SET XACT_ABORT ON; BEGIN TRY BEGIN TRANSACTION;
            DECLARE @previous varbinary(max);
            SELECT @previous = [Value] FROM {_table} WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = @key;
            IF @previous IS NULL
            BEGIN
                SET @previous = @data;
                INSERT INTO {_table} ([Id], [Value], [ExpiresAtTime], [AbsoluteExpiration], [SlidingExpirationInSeconds]) VALUES (@key, @data, @maximum, @maximum, NULL);
            END
            COMMIT TRANSACTION; SELECT @previous;
            END TRY BEGIN CATCH IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION; THROW; END CATCH;
            """;
        var data = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(data, requested.Ticks);
        var connection = new SqlConnection(_connectionString);
        await using var connectionScope = connection.ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = new SqlCommand(sql, connection);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(new SqlParameter("@key", SqlDbType.NVarChar, 449) { Value = GetPhysicalKey(key) });
        command.Parameters.Add(new SqlParameter("@data", SqlDbType.VarBinary, 8) { Value = data });
        command.Parameters.Add(new SqlParameter("@maximum", SqlDbType.DateTimeOffset) { Value = DateTimeOffset.MaxValue });
        var stored = (byte[])(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        if (stored.Length != 8) throw new IncompatibleDistributedScopeException("The distributed retention contract has an incompatible schema.");
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(stored);
        if (ticks <= 0 || ticks > requested.Ticks)
            throw new IncompatibleDistributedScopeException("TagRetention cannot be shorter than this scope's established metadata retention. Use a new KeyPrefix for a new contract.");
        return TimeSpan.FromTicks(ticks);
    }

    private Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        lock (_schemaLock)
        {
            if (_schemaReady is null or { IsFaulted: true } or { IsCanceled: true })
                _schemaReady = InitializeSchemaAsync(cancellationToken);
            return _schemaReady.WaitAsync(cancellationToken);
        }
    }

    private async Task InitializeSchemaAsync(CancellationToken cancellationToken)
    {
        try { await SqlServerTables.CreateIfMissingAsync(_schemaOptions, cancellationToken).ConfigureAwait(false); }
        catch (SqlException exception) when (exception.Number == 50001)
        { throw new IncompatibleDistributedScopeException("The MethodCache SQL schema is incompatible. Apply its explicit migration before starting this version."); }
    }

    private static void AddEpochParameters(SqlCommand command) =>
        command.Parameters.Add(new SqlParameter("@maximum", SqlDbType.DateTimeOffset) { Value = DateTimeOffset.MaxValue });
}
