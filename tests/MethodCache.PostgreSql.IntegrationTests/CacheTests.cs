using System.Buffers;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using MethodCache.TestSupport;
using Microsoft.Extensions.Caching.Distributed;
using Npgsql;

namespace MethodCache.PostgreSql.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public class CacheTests(PostgreSqlFixture fixture)
{
    private static readonly DistributedCacheEntryOptions Minute = new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) };

    [SkippableFact]
    public async Task Values_RoundTripThroughEveryApi()
    {
        var options = fixture.Options("values");
        try
        {
            await using var connection = await CreateAsync(options);
            var cache = new PostgreSqlDistributedCache(connection, options);

            cache.Set("sync", [1, 2, 3], Minute);
            await cache.SetAsync("async", [4, 5], Minute);
            cache.Set("sequence", new ReadOnlySequence<byte>([6, 7]), Minute);
            await cache.SetAsync("segments", Segments([8], [9, 10]), Minute);

            Assert.Equal(new byte[] { 1, 2, 3 }, cache.Get("sync"));
            Assert.Equal(new byte[] { 4, 5 }, await cache.GetAsync("async"));
            var buffer = new ArrayBufferWriter<byte>();
            Assert.True(cache.TryGet("sequence", buffer));
            Assert.Equal(new byte[] { 6, 7 }, buffer.WrittenSpan.ToArray());
            buffer.ResetWrittenCount();
            Assert.True(await cache.TryGetAsync("segments", buffer));
            Assert.Equal(new byte[] { 8, 9, 10 }, buffer.WrittenSpan.ToArray());
            Assert.False(await cache.TryGetAsync("missing", buffer));
            Assert.Null(await cache.GetAsync("missing"));

            cache.Set("sync", [42], Minute);
            Assert.Equal(new byte[] { 42 }, await cache.GetAsync("sync"));
            cache.Refresh("sync");
            await cache.RefreshAsync("async");
            cache.Remove("sync");
            await cache.RemoveAsync("async");
            Assert.Null(cache.Get("sync"));
            Assert.Null(await cache.GetAsync("async"));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task ExpiredValue_IsAMiss()
    {
        var options = fixture.Options("expired");
        try
        {
            await using var connection = await CreateAsync(options);
            var cache = new PostgreSqlDistributedCache(connection, options);
            await cache.SetAsync("past", [1], new DistributedCacheEntryOptions { AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(-1) });
            await cache.SetAsync("forever", [2], new DistributedCacheEntryOptions());

            Assert.Null(await cache.GetAsync("past"));
            Assert.False(cache.TryGet("past", new ArrayBufferWriter<byte>()));
            Assert.Equal(new byte[] { 2 }, await cache.GetAsync("forever"));
            Assert.Equal("infinity", await PostgreSqlFixture.ScalarAsync<string>(options.ConnectionString, $"SELECT max(expires_at)::text FROM {options.QualifiedTableName}"));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task LargeValue_RoundTrips_CompressedWithLz4WhereAvailable_AndSmallerValuesStayInTheRow()
    {
        var options = fixture.Options("large");
        try
        {
            await using var connection = await CreateAsync(options);
            var cache = new PostgreSqlDistributedCache(connection, options);
            var value = new byte[3 * 1024 * 1024];
            for (var i = 0; i < value.Length; i++) value[i] = (byte)(i % 251 < 200 ? 'a' : i % 7);

            await cache.SetAsync("large", value, Minute);

            Assert.Equal(value, await cache.GetAsync("large"));
            var buffer = new ArrayBufferWriter<byte>();
            Assert.True(await cache.TryGetAsync("large", buffer));
            Assert.True(buffer.WrittenSpan.SequenceEqual(value));
            var lz4 = await PostgreSqlFixture.ScalarAsync<bool>(options.ConnectionString,
                "SELECT EXISTS (SELECT 1 FROM pg_settings WHERE name = 'default_toast_compression' AND 'lz4' = ANY(enumvals))");
            if (lz4)
            {
                Assert.Equal("lz4", await PostgreSqlFixture.ScalarAsync<string>(options.ConnectionString,
                    $"SELECT pg_column_compression(value) FROM {options.QualifiedTableName}"));
            }

            // 6 KB that does not compress: past the 2 KB TOAST threshold, yet still read from the row itself
            var incompressible = new byte[6 * 1024];
            new Random(7).NextBytes(incompressible);
            await cache.SetAsync("row", incompressible, Minute);
            Assert.Equal(incompressible, await cache.GetAsync("row"));
            Assert.Equal("id:p,value:m", await PostgreSqlFixture.ScalarAsync<string>(options.ConnectionString,
                $"SELECT string_agg(attname || ':' || attstorage::text, ',' ORDER BY attname) FROM pg_attribute WHERE attrelid = '{options.QualifiedTableName}'::regclass AND attname IN ('id', 'value')"));
            // Only the 3 MB value is stored out of the row: neither the 6 KB value nor either key
            var toast = await PostgreSqlFixture.ScalarAsync<string>(options.ConnectionString,
                $"SELECT reltoastrelid::regclass::text FROM pg_class WHERE oid = '{options.QualifiedTableName}'::regclass");
            Assert.Equal(1L, await PostgreSqlFixture.ScalarAsync<long>(options.ConnectionString, $"SELECT count(DISTINCT chunk_id) FROM {toast}"));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task ValueAndMessageWrites_CommitAsynchronously_WithoutChangingThePooledConnection()
    {
        var options = fixture.Options("durability");
        options.ConnectionString = new NpgsqlConnectionStringBuilder(options.ConnectionString) { MaxPoolSize = 1 }.ConnectionString;
        try
        {
            await using var connection = await CreateAsync(options);
            var cache = new PostgreSqlDistributedCache(connection, options);

            await using var backplane = new PostgreSqlBackplane(connection, options);

            await cache.SetAsync("key", [1], Minute);
            await backplane.PublishInvalidationAsync("key");

            // One connection in the pool, never reset when it comes back: the next command runs on the one the writes
            // used, and still finds the server's setting
            await using var show = connection.DataSource.CreateCommand("SHOW synchronous_commit");
            Assert.Equal("on", await show.ExecuteScalarAsync());
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task ReadEntry_ReturnsTheValueAndItsRemovalStampInOneRead()
    {
        var options = fixture.Options("entry");
        try
        {
            await using var connection = await CreateAsync(options);
            var cache = new PostgreSqlDistributedCache(connection, options);

            Assert.Equal(new DistributedRead(null, null), await cache.ReadEntryAsync("value", "removal"));
            await cache.SetAsync("value", [7], Minute);
            var read = await cache.ReadEntryAsync("value", "removal");
            Assert.Equal(new byte[] { 7 }, read.Value);
            Assert.Null(read.RemovalStamp);

            await cache.WriteInvalidationStampAsync("removal", 42, TimeSpan.FromMinutes(1));
            read = await cache.ReadEntryAsync("value", "removal");
            Assert.Equal(new byte[] { 7 }, read.Value);
            Assert.Equal(42, InvalidationStampContract.Read(read.RemovalStamp!));

            await cache.RemoveAsync("value");
            read = await cache.ReadEntryAsync("value", "removal");
            Assert.Null(read.Value);
            Assert.Equal(42, InvalidationStampContract.Read(read.RemovalStamp!));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableTheory]
    [InlineData("tag", "tag ")]
    [InlineData("\0clear", "\0\0clear")]
    [InlineData("A", "a")]
    public async Task DistinctKeys_DoNotAlias(string first, string second)
    {
        var options = fixture.Options("alias");
        try
        {
            await using var connection = await CreateAsync(options);
            var cache = new PostgreSqlDistributedCache(connection, options);
            await cache.SetAsync(first, [1], Minute);
            await cache.SetAsync(second, [2], Minute);
            await cache.SetAsync(new string((char)0xd800, 1), [3], Minute);
            await cache.SetAsync("�", [4], Minute);

            Assert.Equal(new byte[] { 1 }, await cache.GetAsync(first));
            Assert.Equal(new byte[] { 2 }, await cache.GetAsync(second));
            Assert.Equal(new byte[] { 3 }, await cache.GetAsync(new string((char)0xd800, 1)));
            Assert.Equal(new byte[] { 4 }, await cache.GetAsync("�"));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task Sweep_DeletesExpiredEntriesInBatches_AndKeepsLiveOnes()
    {
        var options = fixture.Options("sweep");
        try
        {
            await using var connection = await CreateAsync(options);
            var cache = new PostgreSqlDistributedCache(connection, options);
            await cache.SetAsync("live", [1], Minute);
            await PostgreSqlFixture.ExecuteAsync(options.ConnectionString, $"""
                INSERT INTO {options.QualifiedTableName} (id, value, expires_at)
                SELECT sha256(int4send(i)), '\x01', now() - interval '1 minute' FROM generate_series(1, 2500) AS i
                """);
            var sql = $"""
                DELETE FROM {options.QualifiedTableName} WHERE id IN (
                    SELECT id FROM {options.QualifiedTableName} WHERE expires_at < now()
                    LIMIT {PostgreSqlTables.SweepBatchSize} FOR UPDATE SKIP LOCKED)
                """;

            Assert.Equal(2500, await PostgreSqlTables.SweepOnceAsync(connection.DataSource, sql, CancellationToken.None));
            Assert.Equal(new byte[] { 1 }, await cache.GetAsync("live"));
            Assert.Equal(1L, await PostgreSqlFixture.ScalarAsync<long>(options.ConnectionString, $"SELECT count(*) FROM {options.QualifiedTableName}"));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    internal static async Task<PostgreSqlConnection> CreateAsync(PostgreSqlOptions options)
    {
        var connection = new PostgreSqlConnection(options.ConnectionString);
        await PostgreSqlTables.CreateIfMissingAsync(connection.DataSource, options, CancellationToken.None);
        return connection;
    }

    private static ReadOnlySequence<byte> Segments(byte[] first, byte[] second)
    {
        var start = new Segment(first);
        var end = start.Append(second);
        return new ReadOnlySequence<byte>(start, 0, end, second.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(byte[] memory) => Memory = memory;

        public Segment Append(byte[] memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
