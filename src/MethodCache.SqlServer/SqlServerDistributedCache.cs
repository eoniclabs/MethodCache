using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using MethodCache.Extensibility;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.SqlServer;

namespace MethodCache.SqlServer;

/// <summary>Keeps the standard SQL cache layout and stores invalidation stamps with a serializable key lock.</summary>
internal sealed class SqlServerDistributedCache : IAtomicInvalidationStore
{
    private readonly SqlServerCache _inner;
    private readonly string _connectionString;
    private readonly string _table;

    internal SqlServerDistributedCache(SqlServerOptions options)
    {
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
        var stamp = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(stamp, timestamp);
        var now = DateTimeOffset.UtcNow;
        if (retention > DateTimeOffset.MaxValue - now)
            throw new ArgumentOutOfRangeException(nameof(retention), "The requested retention exceeds SQL Server's maximum datetimeoffset expiration.");
        var expires = now + retention;
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
                IF @exists = 0
                    INSERT INTO {_table} ([Id], [Value], [ExpiresAtTime], [AbsoluteExpiration], [SlidingExpirationInSeconds])
                    VALUES (@key, @stamp, @expires, @expires, NULL);
                ELSE
                    UPDATE {_table} SET
                        [Value] = CASE WHEN @oldExpires <= @now OR @previousStamp IS NULL OR @previousStamp < @timestamp THEN @stamp ELSE SUBSTRING(@previous, 1, 8) END,
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
        command.Parameters.Add(new SqlParameter("@key", SqlDbType.NVarChar, 449) { Value = physicalKey });
        command.Parameters.Add(new SqlParameter("@stamp", SqlDbType.VarBinary, 8) { Value = stamp });
        command.Parameters.Add(new SqlParameter("@timestamp", SqlDbType.BigInt) { Value = timestamp });
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTimeOffset) { Value = now });
        command.Parameters.Add(new SqlParameter("@expires", SqlDbType.DateTimeOffset) { Value = expires });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string GetPhysicalKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        // SQL text comparisons can equate NULs, trailing spaces and normalized Unicode even with CS_AS.
        // Hash exact UTF-16 code units so data and stamp operations use the same ordinal identity. The ID is always
        // 67 characters, so a logical key of any length fits the nvarchar(449) column.
        var bytes = MethodCache.Infrastructure.LosslessUtf16.GetBytes(key);
        return "mc:" + Convert.ToHexString(SHA256.HashData(bytes));
    }

}
