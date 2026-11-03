using System.Buffers.Binary;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace MethodCache.PostgreSql;

/// <summary>
/// Invalidation stamps and HTTP epochs, each written by one <c>INSERT ... ON CONFLICT DO UPDATE</c>: PostgreSQL
/// locks the existing row, so concurrent writers on any instance apply their changes one after the other.
/// </summary>
/// <remarks>
/// A stamp is eight bytes, the invalidation time in Unix milliseconds, signed and little-endian. An epoch row is
/// that stamp followed by version 1, sixteen initialization bytes and a big-endian 64-bit revision, as on Redis
/// and SQL Server. PostgreSQL evaluates the <c>WHERE</c> of <c>ON CONFLICT DO UPDATE</c> before the new values,
/// so a row with an unknown layout is neither changed nor read past its end; the statement returns no row and the
/// caller reports the incompatible schema.
/// </remarks>
internal sealed partial class PostgreSqlDistributedCache
{
    private readonly string _ensureEpochsReadSql;
    private readonly string _ensureEpochsWriteSql;
    private readonly string _advanceEpochSql;
    private readonly string _retentionSql;

    // A stamp of long.MinValue (the key was never invalidated) followed by the epoch marker
    private const string EmptyStampAndMarker = @"'\x000000000000008001'::bytea";

    /// <summary>The signed little-endian 64-bit stamp at the start of <paramref name="value"/>.</summary>
    internal static string Stamp(string value) =>
        $"(get_byte({value}, 0)::int8 | (get_byte({value}, 1)::int8 << 8) | (get_byte({value}, 2)::int8 << 16) | " +
        $"(get_byte({value}, 3)::int8 << 24) | (get_byte({value}, 4)::int8 << 32) | (get_byte({value}, 5)::int8 << 40) | " +
        $"(get_byte({value}, 6)::int8 << 48) | (get_byte({value}, 7)::int8 << 56))";

    /// <summary>Whether <paramref name="value"/> is epoch metadata. Never reads past the end of a short value.</summary>
    internal static string Marked(string value) => $"(CASE WHEN length({value}) >= 33 THEN get_byte({value}, 8) = 1 ELSE false END)";

    /// <summary>Whether the value of <paramref name="row"/> may be changed: expired, a plain stamp or epoch metadata.</summary>
    private static string Known(string row) => $"({row}.expires_at <= now() OR length({row}.value) = 8 OR {Marked(row + ".value")})";

    // The revision as a signed 64-bit number of the same bits: -1 is the unsigned maximum
    private static string Revision(string value) => $"('x' || encode(substring({value} from 26 for 8), 'hex'))::bit(64)::int8";

    /// <summary>
    /// Epoch metadata <paramref name="value"/> with <paramref name="stamp"/> in front and its revision advanced. After
    /// the last revision the epoch starts again at zero under the new initialization bytes in <paramref name="seed"/>.
    /// </summary>
    internal static string Advance(string value, string stamp, string seed) => $"""
        (CASE WHEN {Revision(value)} = -1 THEN {stamp} || '\x01'::bytea || {seed} || '\x0000000000000000'::bytea
              WHEN {Revision(value)} = 9223372036854775807 THEN {stamp} || substring({value} from 9 for 17) || '\x8000000000000000'::bytea
              ELSE {stamp} || substring({value} from 9 for 17) || int8send({Revision(value)} + 1) END || substring({value} from 34))
        """;

    // $1 id, $2 stamp bytes, $3 stamp, $4 expiry, $5 now, $6 initialization bytes for a wrapped epoch. Keeps the newest
    // stamp, never shortens an unexpired expiration, and keeps an epoch row permanent while advancing its revision.
    private static string StampSql(string table) => $"""
        INSERT INTO {table} AS c (id, value, expires_at) VALUES ($1, $2, $4)
        ON CONFLICT (id) DO UPDATE SET
            value = CASE
                WHEN c.expires_at <= $5 THEN $2
                WHEN {Marked("c.value")} THEN {Advance("c.value", $"(CASE WHEN {Stamp("c.value")} < $3 THEN $2 ELSE substring(c.value from 1 for 8) END)", "$6")}
                WHEN {Stamp("c.value")} < $3 THEN $2
                ELSE c.value END,
            expires_at = CASE
                WHEN c.expires_at > $5 AND {Marked("c.value")} THEN 'infinity'::timestamptz
                ELSE GREATEST(c.expires_at, $4) END
        WHERE c.expires_at <= $5 OR length(c.value) = 8 OR {Marked("c.value")}
        RETURNING 1
        """;

    // Reads initialized, permanent epochs without locking or writing anything, so readers never queue on each other
    private static string EnsureEpochsReadSql(string table) => $"""
        SELECT k.position, t.value FROM unnest($1::bytea[]) WITH ORDINALITY AS k(id, position)
        JOIN {table} AS t ON t.id = k.id
        WHERE t.expires_at = 'infinity' AND {Marked("t.value")}
        """;

    // Distinct ids in sorted order, so concurrent batches lock rows in one order. An existing stamp is kept in front
    // of the new epoch; an expired row starts over.
    private static string EnsureEpochsWriteSql(string table) => $"""
        INSERT INTO {table} AS c (id, value, expires_at)
        SELECT k.id, {EmptyStampAndMarker} || uuid_send(gen_random_uuid()) || '\x0000000000000000'::bytea, 'infinity'
        FROM unnest($1::bytea[]) AS k(id) ORDER BY k.id
        ON CONFLICT (id) DO UPDATE SET
            value = CASE
                WHEN c.expires_at <= now() THEN EXCLUDED.value
                WHEN length(c.value) = 8 THEN c.value || substring(EXCLUDED.value from 9)
                ELSE c.value END,
            expires_at = 'infinity'
        WHERE {Known("c")}
        RETURNING id, value
        """;

    // $1 id, $2 initialization bytes: a new epoch starts at revision 1
    private static string AdvanceEpochSql(string table) => $"""
        INSERT INTO {table} AS c (id, value, expires_at)
        VALUES ($1, {EmptyStampAndMarker} || $2 || '\x0000000000000001'::bytea, 'infinity')
        ON CONFLICT (id) DO UPDATE SET
            value = CASE
                WHEN c.expires_at <= now() THEN EXCLUDED.value
                WHEN length(c.value) = 8 THEN c.value || '\x01'::bytea || $2 || '\x0000000000000001'::bytea
                ELSE {Advance("c.value", "substring(c.value from 1 for 8)", "$2")} END,
            expires_at = 'infinity'
        WHERE {Known("c")}
        RETURNING value
        """;

    // The first contract wins; the no-op update locks and returns the stored one
    private static string RetentionSql(string table) => $"""
        INSERT INTO {table} AS c (id, value, expires_at) VALUES ($1, $2, 'infinity')
        ON CONFLICT (id) DO UPDATE SET expires_at = c.expires_at
        RETURNING value
        """;

    public async ValueTask<CacheEpoch[]> EnsureEpochsAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();
        if (keys.Count > 128) throw new ArgumentOutOfRangeException(nameof(keys), "A metadata batch may contain at most 128 keys.");
        if (keys.Count == 0) return [];
        foreach (var key in keys) ArgumentNullException.ThrowIfNull(key);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var ids = new byte[keys.Count][];
        for (var i = 0; i < ids.Length; i++) ids[i] = GetPhysicalKey(keys[i]);

        var result = new CacheEpoch[keys.Count];
        var found = 0;
        using (var read = _dataSource.CreateCommand(_ensureEpochsReadSql))
        {
            read.Parameters.Add(ByteaArray(ids));
            var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    result[reader.GetInt64(0) - 1] = ReadEpoch(reader.GetFieldValue<byte[]>(1));
                    found++;
                }
            }
        }

        if (found == ids.Length) return result;

        // At least one epoch is missing, expired, finite or a plain stamp: initialize the whole batch in one statement
        var distinct = ids.Distinct(ByteArrayComparer.Instance).Order(ByteArrayComparer.Instance).ToArray();
        var written = new Dictionary<byte[], CacheEpoch>(distinct.Length, ByteArrayComparer.Instance);
        using (var write = _dataSource.CreateCommand(_ensureEpochsWriteSql))
        {
            write.Parameters.Add(ByteaArray(distinct));
            var reader = await write.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    written[reader.GetFieldValue<byte[]>(0)] = ReadEpoch(reader.GetFieldValue<byte[]>(1));
            }
        }

        for (var i = 0; i < ids.Length; i++)
        {
            if (!written.TryGetValue(ids[i], out result[i]))
                throw new IncompatibleDistributedScopeException("The distributed epoch metadata has an incompatible schema.");
        }

        return result;
    }

    public async ValueTask<CacheEpoch> AdvanceEpochAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        using var command = Command(_advanceEpochSql, GetPhysicalKey(key));
        command.Parameters.Add(Bytea(Guid.NewGuid().ToByteArray()));
        return ReadEpoch(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[]);
    }

    public async ValueTask<TimeSpan> EnsureRetentionContractAsync(string key, TimeSpan requested, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (requested <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requested));
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var data = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(data, requested.Ticks);
        using var command = Command(_retentionSql, GetPhysicalKey(key));
        command.Parameters.Add(Bytea(data));
        var stored = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[];
        if (stored is not { Length: 8 }) throw new IncompatibleDistributedScopeException("The distributed retention contract has an incompatible schema.");
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(stored);
        if (ticks <= 0 || ticks > requested.Ticks)
            throw new IncompatibleDistributedScopeException("TagRetention cannot be shorter than this scope's established metadata retention. Use a new KeyPrefix for a new contract.");
        return TimeSpan.FromTicks(ticks);
    }

    private static CacheEpoch ReadEpoch(byte[]? data)
    {
        if (data == null || !EpochMetadata.TryRead(data, out var epoch))
            throw new IncompatibleDistributedScopeException("The distributed epoch metadata has an incompatible schema.");
        return epoch;
    }

    private static NpgsqlParameter<byte[][]> ByteaArray(byte[][] values) => new()
    { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, TypedValue = values };

    /// <summary>Orders and compares ids by their bytes, as PostgreSQL orders <c>bytea</c>.</summary>
    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>, IComparer<byte[]>
    {
        internal static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj) => BinaryPrimitives.ReadInt32LittleEndian(obj);

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
