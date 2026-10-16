using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using MethodCache.Extensibility;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.SqlServer;

namespace MethodCache.SqlServer;

/// <summary>Keeps the standard SQL cache layout and stores invalidation stamps with a serializable key lock.</summary>
internal sealed partial class SqlServerDistributedCache : IAtomicInvalidationStore, IBatchedDistributedStore, IDistributedEpochStore
{
    private readonly SqlServerCache _inner;
    private readonly SqlServerOptions _schemaOptions;
    private readonly object _schemaLock = new();
    private Task? _schemaReady;
    private readonly string _connectionString;
    private readonly string _table;

    internal SqlServerDistributedCache(SqlServerOptions options)
    {
        _schemaOptions = options;
        _inner = new SqlServerCache(Microsoft.Extensions.Options.Options.Create(
            Microsoft.Extensions.DependencyInjection.MethodCacheSqlServerServiceCollectionExtensions.CacheOptions(options)));
        _connectionString = options.ConnectionString;
        _table = options.QualifiedTableName;
    }

    public byte[]? Get(string key) => _inner.Get(GetPhysicalKey(key));
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => _inner.GetAsync(GetPhysicalKey(key), token);
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _inner.Set(GetPhysicalKey(key), value, options);
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => _inner.SetAsync(GetPhysicalKey(key), value, options, token);
    public void Refresh(string key) => _inner.Refresh(GetPhysicalKey(key));
    public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(GetPhysicalKey(key), token);
    public void Remove(string key) => _inner.Remove(GetPhysicalKey(key));
    public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(GetPhysicalKey(key), token);

    public async Task WriteInvalidationStampAsync(string key, long timestamp, TimeSpan retention, CancellationToken cancellationToken = default)
    {
        var physicalKey = GetPhysicalKey(key);
        if (retention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var stamp = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(stamp, timestamp);
        var now = DateTimeOffset.UtcNow;
        // A retention past the latest time SQL Server stores means "keep it", not a failure
        var expires = retention < DateTimeOffset.MaxValue - now ? now + retention : DateTimeOffset.MaxValue;
        var sql = $"""
            SET XACT_ABORT ON;
            BEGIN TRY
                BEGIN TRANSACTION;
                DECLARE @previous varbinary(max), @oldExpires datetimeoffset, @exists bit = 0;
                SELECT @previous = [Value], @oldExpires = [ExpiresAtTime], @exists = 1
                FROM {_table} WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = @key;
                DECLARE @previousStamp bigint = NULL;
                IF DATALENGTH(@previous) >= 8
                    SET @previousStamp = CONVERT(bigint,
                        SUBSTRING(@previous, 8, 1) + SUBSTRING(@previous, 7, 1) + SUBSTRING(@previous, 6, 1) + SUBSTRING(@previous, 5, 1) +
                        SUBSTRING(@previous, 4, 1) + SUBSTRING(@previous, 3, 1) + SUBSTRING(@previous, 2, 1) + SUBSTRING(@previous, 1, 1));
                DECLARE @marked bit = CASE WHEN @oldExpires > @now AND DATALENGTH(@previous) >= 33 AND SUBSTRING(@previous, 9, 1) = 0x01 THEN 1 ELSE 0 END;
                DECLARE @data varbinary(max) = @previous;
                IF @marked = 1
                BEGIN
                    {AdvanceEpochSql}
                    SET @data = CASE WHEN @previousStamp < @timestamp THEN @stamp ELSE SUBSTRING(@previous, 1, 8) END + SUBSTRING(@data, 9, 2147483647);
                    SET @expires = @maximum;
                END
                ELSE
                BEGIN
                    IF @oldExpires > @now AND DATALENGTH(@previous) <> 8 THROW 50001, 'MethodCache invalidation metadata schema mismatch.', 1;
                    SET @data = CASE WHEN @oldExpires <= @now OR @previousStamp IS NULL OR @previousStamp < @timestamp THEN @stamp ELSE SUBSTRING(@previous, 1, 8) END;
                END
                IF @exists = 0
                    INSERT INTO {_table} ([Id], [Value], [ExpiresAtTime], [AbsoluteExpiration], [SlidingExpirationInSeconds])
                    VALUES (@key, @data, @expires, @expires, NULL);
                ELSE
                    UPDATE {_table} SET
                        [Value] = @data,
                        [ExpiresAtTime] = CASE WHEN @oldExpires > @expires THEN @oldExpires ELSE @expires END,
                        [AbsoluteExpiration] = CASE WHEN @oldExpires > @expires THEN @oldExpires ELSE @expires END,
                        [SlidingExpirationInSeconds] = NULL
                    WHERE [Id] = @key;
                COMMIT TRANSACTION;
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                THROW;
            END CATCH;
            """;
        var connection = new SqlConnection(_connectionString);
        await using var connectionScope = connection.ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = new SqlCommand(sql, connection);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(new SqlParameter("@seed", SqlDbType.VarBinary, 16) { Value = Guid.NewGuid().ToByteArray() });
        command.Parameters.Add(new SqlParameter("@maximum", SqlDbType.DateTimeOffset) { Value = DateTimeOffset.MaxValue });
        command.Parameters.Add(new SqlParameter("@key", SqlDbType.NVarChar, 449) { Value = physicalKey });
        command.Parameters.Add(new SqlParameter("@stamp", SqlDbType.VarBinary, 8) { Value = stamp });
        command.Parameters.Add(new SqlParameter("@timestamp", SqlDbType.BigInt) { Value = timestamp });
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTimeOffset) { Value = now });
        command.Parameters.Add(new SqlParameter("@expires", SqlDbType.DateTimeOffset) { Value = expires });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static string GetPhysicalKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        // SQL text comparisons can equate NULs, trailing spaces and normalized Unicode even with CS_AS.
        // Hash exact UTF-16 code units so data and stamp operations use the same ordinal identity. The ID is always
        // 67 characters, so a logical key of any length fits the nvarchar(449) column.
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        MethodCache.Infrastructure.LosslessUtf16.Sha256(key, hash);
        Span<char> id = stackalloc char[3 + 2 * SHA256.HashSizeInBytes];
        "mc:".CopyTo(id);
        Convert.TryToHexString(hash, id[3..], out _);
        return new string(id);
    }

}
