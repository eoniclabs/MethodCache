using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Caching.Distributed;
using Npgsql;
using NpgsqlTypes;

namespace MethodCache.PostgreSql;

/// <summary>
/// MethodCache's own PostgreSQL cache: entries and invalidation metadata in one table, keyed by the SHA-256 of the
/// exact key. Each operation is one statement and one round trip.
/// </summary>
/// <remarks>
/// New values, and values that replace an expired one, commit asynchronously (<c>synchronous_commit = off</c> for that
/// statement only): PostgreSQL acknowledges them before flushing its log, so a crash can lose the last fraction of a
/// second of them, which then become misses. A value that replaces a live one takes a second statement that commits as
/// the server's setting says, like invalidation stamps, epochs and the retention contract. The log is sequential, so a
/// stamp that survives a crash never leaves an older value it invalidated behind.
/// </remarks>
internal sealed partial class PostgreSqlDistributedCache : IBufferDistributedCache, IAtomicInvalidationStore, IBatchedDistributedStore, IDistributedEpochStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlOptions _schemaOptions;
    private readonly object _schemaLock = new();
    private Task? _schemaReady;

    private readonly string _getSql;
    private readonly string _readEntrySql;
    private readonly string _setSql;
    private readonly string _replaceSql;
    private readonly string _removeSql;
    private readonly string _stampSql;

    internal PostgreSqlDistributedCache(PostgreSqlConnection connection, PostgreSqlOptions options)
    {
        _dataSource = connection.DataSource;
        _schemaOptions = options;
        var table = options.QualifiedTableName;
        _getSql = $"SELECT value FROM {table} WHERE id = $1 AND expires_at > now()";
        // Positions instead of ids: reading an id back would allocate an array per row
        _readEntrySql = $"SELECT id = $2, value FROM {table} WHERE id IN ($1, $2) AND expires_at > now()";
        // set_config(..., true) lasts for this statement's transaction only, so the pooled connection is unchanged. A
        // new value, or one that replaces an expired value, commits without waiting for the disk: lost in a crash, it is
        // a miss. A live value is left alone (no row written), since losing its replacement would bring it back.
        _setSql = $"""
            WITH durability AS (SELECT set_config('synchronous_commit', 'off', true))
            INSERT INTO {table} AS c (id, value, expires_at) SELECT $1, $2, $3 FROM durability
            ON CONFLICT (id) DO UPDATE SET value = EXCLUDED.value, expires_at = EXCLUDED.expires_at
            WHERE c.expires_at <= now()
            """;
        // Replaces a live value, committing as the server's setting says. It upserts, since the row may be gone by now.
        _replaceSql = $"""
            INSERT INTO {table} (id, value, expires_at) VALUES ($1, $2, $3)
            ON CONFLICT (id) DO UPDATE SET value = EXCLUDED.value, expires_at = EXCLUDED.expires_at
            """;
        _removeSql = $"DELETE FROM {table} WHERE id = $1";
        _stampSql = StampSql(table);
        _ensureEpochsReadSql = EnsureEpochsReadSql(table);
        _ensureEpochsWriteSql = EnsureEpochsWriteSql(table);
        _advanceEpochSql = AdvanceEpochSql(table);
        _retentionSql = RetentionSql(table);
    }

    /// <summary>The statement that writes a new value, for tests that run it in a transaction of their own.</summary>
    internal string SetSql => _setSql;

    /// <summary>The statement that replaces a live value, for tests that run it in a transaction of their own.</summary>
    internal string ReplaceSql => _replaceSql;

    public byte[]? Get(string key)
    {
        using var command = Command(_getSql, Id(key));
        return command.ExecuteScalar() as byte[];
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        using var command = Command(_getSql, Id(key));
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) as byte[];
    }

    public bool TryGet(string key, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var command = Command(_getSql, Id(key));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return false;
        Copy(reader, destination);
        return true;
    }

    public async ValueTask<bool> TryGetAsync(string key, IBufferWriter<byte> destination, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var command = Command(_getSql, Id(key));
        var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return false;
            Copy(reader, destination);
            return true;
        }
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        Write(key, value, options);
    }

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return WriteAsync(key, value, options, token);
    }

    public void Set(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options)
        => Write(key, Memory(value), options);

    public ValueTask SetAsync(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options, CancellationToken token = default)
        => new(WriteAsync(key, Memory(value), options, token));

    /// <summary>Writes a new or expired value in one statement; a live value takes a second, durable one.</summary>
    private void Write(string key, ReadOnlyMemory<byte> value, DistributedCacheEntryOptions options)
    {
        using var command = SetCommand(key, value, options);
        if (command.ExecuteNonQuery() != 0) return;
        command.CommandText = _replaceSql;
        command.ExecuteNonQuery();
    }

    private async Task WriteAsync(string key, ReadOnlyMemory<byte> value, DistributedCacheEntryOptions options, CancellationToken token)
    {
        using var command = SetCommand(key, value, options);
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 0) return;
        command.CommandText = _replaceSql;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    /// <summary>Does nothing: entries only have absolute expirations.</summary>
    public void Refresh(string key) => ArgumentNullException.ThrowIfNull(key);

    /// <summary>Does nothing: entries only have absolute expirations.</summary>
    public Task RefreshAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Task.CompletedTask;
    }

    public void Remove(string key)
    {
        using var command = Command(_removeSql, Id(key));
        command.ExecuteNonQuery();
    }

    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        using var command = Command(_removeSql, Id(key));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    public async ValueTask<DistributedRead> ReadEntryAsync(string valueKey, string removalKey, CancellationToken cancellationToken = default)
    {
        using var command = Command(_readEntrySql, Id(valueKey));
        command.Parameters.Add(Bytea(GetPhysicalKey(removalKey)));
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            byte[]? value = null;
            byte[]? removal = null;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetBoolean(0)) removal = reader.GetFieldValue<byte[]>(1);
                else value = reader.GetFieldValue<byte[]>(1);
            }

            return new DistributedRead(value, removal);
        }
    }

    public async Task WriteInvalidationStampAsync(string key, long timestamp, TimeSpan retention, CancellationToken cancellationToken = default)
    {
        var id = GetPhysicalKey(key);
        if (retention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var stamp = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(stamp, timestamp);
        var now = DateTime.UtcNow;
        // A retention past the latest time .NET represents means "keep it", not a failure
        var expires = retention < DateTime.MaxValue - now ? now + retention : DateTime.MaxValue;
        using var command = Command(_stampSql, id);
        command.Parameters.Add(Bytea(stamp));
        command.Parameters.Add(new NpgsqlParameter<long> { NpgsqlDbType = NpgsqlDbType.Bigint, TypedValue = timestamp });
        command.Parameters.Add(Timestamp(expires));
        command.Parameters.Add(Timestamp(now));
        command.Parameters.Add(Bytea(Guid.NewGuid().ToByteArray()));
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) == null)
            throw new IncompatibleDistributedScopeException("MethodCache invalidation metadata has an incompatible schema.");
    }

    /// <summary>
    /// The row id of <paramref name="key"/>: the SHA-256 of its exact UTF-16 code units. PostgreSQL text can't hold
    /// NUL characters or lone surrogates, and the hash gives every key, however long, the same small index entry.
    /// </summary>
    internal static byte[] GetPhysicalKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var id = new byte[SHA256.HashSizeInBytes];
        MethodCache.Infrastructure.LosslessUtf16.Sha256(key, id);
        return id;
    }

    private NpgsqlCommand Command(string sql, byte[] id)
    {
        var command = _dataSource.CreateCommand(sql);
        command.Parameters.Add(Bytea(id));
        return command;
    }

    private NpgsqlCommand SetCommand(string key, ReadOnlyMemory<byte> value, DistributedCacheEntryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var command = Command(_setSql, Id(key));
        command.Parameters.Add(new NpgsqlParameter<ReadOnlyMemory<byte>> { NpgsqlDbType = NpgsqlDbType.Bytea, TypedValue = value });
        command.Parameters.Add(Timestamp(Expiration(options)));
        return command;
    }

    internal static DateTime Expiration(DistributedCacheEntryOptions options)
    {
        if (options.SlidingExpiration != null)
            throw new NotSupportedException("MethodCache's PostgreSQL cache stores absolute expirations only.");
        if (options.AbsoluteExpiration is { } absolute) return absolute.UtcDateTime;
        if (options.AbsoluteExpirationRelativeToNow is { } relative)
        {
            var now = DateTime.UtcNow;
            return relative < DateTime.MaxValue - now ? now + relative : DateTime.MaxValue;
        }

        return DateTime.MaxValue;
    }

    private static byte[] Id(string key) => GetPhysicalKey(key);

    // MethodCache writes one contiguous buffer; a sequence of several segments is copied once
    private static ReadOnlyMemory<byte> Memory(ReadOnlySequence<byte> value) => value.IsSingleSegment ? value.First : value.ToArray();

    private static void Copy(NpgsqlDataReader reader, IBufferWriter<byte> destination)
    {
        // The row is already in Npgsql's buffer; this copies it once, into the caller's buffer
        var length = checked((int)reader.GetBytes(0, 0, null, 0, 0));
        var target = destination.GetSpan(length);
        using var stream = reader.GetStream(0);
        stream.ReadExactly(target[..length]);
        destination.Advance(length);
    }

    internal static NpgsqlParameter<byte[]> Bytea(byte[] value) => new() { NpgsqlDbType = NpgsqlDbType.Bytea, TypedValue = value };

    // DateTime.MaxValue is stored as 'infinity'
    private static NpgsqlParameter<DateTime> Timestamp(DateTime value) => new()
    { NpgsqlDbType = NpgsqlDbType.TimestampTz, TypedValue = DateTime.SpecifyKind(value, DateTimeKind.Utc) };

    private Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        lock (_schemaLock)
        {
            if (_schemaReady is null or { IsFaulted: true } or { IsCanceled: true })
                _schemaReady = PostgreSqlTables.CreateIfMissingAsync(_dataSource, _schemaOptions, CancellationToken.None);
            return _schemaReady.WaitAsync(cancellationToken);
        }
    }
}
