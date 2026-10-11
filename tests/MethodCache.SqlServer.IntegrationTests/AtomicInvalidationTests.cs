using MethodCache.Extensibility;
using MethodCache.SqlServer;
using MethodCache.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Distributed;

namespace MethodCache.SqlServer.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public class AtomicInvalidationTests(SqlServerFixture fixture)
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentCacheInstances_PreserveSignedMaximumAndConcurrentStamps(bool concurrent)
    {
        var options = Options();
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            var first = new SqlServerDistributedCache(options);
            var second = new SqlServerDistributedCache(options);
            if (concurrent) await InvalidationStampContract.AssertConcurrentMaximumAsync(first, second, "stamp");
            else await InvalidationStampContract.AssertMaximumAsync(first, second, "stamp");
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task OlderStampWithShortRetention_DoesNotShortenNewerRetention()
    {
        var options = Options();
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            var cache = new SqlServerDistributedCache(options);
            await cache.WriteInvalidationStampAsync("stamp", 20, TimeSpan.FromDays(1));
            await using var connection = new SqlConnection(options.ConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand($"SELECT ExpiresAtTime FROM {options.QualifiedTableName};", connection);
            var before = (DateTimeOffset)(await command.ExecuteScalarAsync())!;
            await cache.WriteInvalidationStampAsync("stamp", 10, TimeSpan.FromMinutes(1));
            Assert.Equal(before, (DateTimeOffset)(await command.ExecuteScalarAsync())!);
            Assert.Equal(20, InvalidationStampContract.Read(Assert.IsType<byte[]>(await cache.GetAsync("stamp"))));
            command.CommandText = $"UPDATE {options.QualifiedTableName} SET ExpiresAtTime = '2000-01-01T00:00:00+00:00';";
            await command.ExecuteNonQueryAsync();
            await cache.WriteInvalidationStampAsync("stamp", 5, TimeSpan.FromMinutes(1));
            Assert.Equal(5, InvalidationStampContract.Read(Assert.IsType<byte[]>(await cache.GetAsync("stamp"))));
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }

    private SqlServerOptions Options() => new()
    {
        ConnectionString = fixture.RequireConnectionString(),
        SchemaName = "stamp_" + Guid.NewGuid().ToString("N")
    };

    [SkippableTheory]
    [InlineData("tag", "tag ")]
    [InlineData("\0clear", "\0\0clear")]
    [InlineData("é", "é")]
    public async Task DistinctLogicalMetadataKeys_DoNotAliasUnderSqlCollation(string first, string second)
    {
        var options = Options();
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            var cache = new SqlServerDistributedCache(options);
            await cache.WriteInvalidationStampAsync(first, 20, TimeSpan.FromMinutes(1));
            await cache.WriteInvalidationStampAsync(second, 10, TimeSpan.FromMinutes(1));
            Assert.Equal(20, InvalidationStampContract.Read(Assert.IsType<byte[]>(await cache.GetAsync(first))));
            Assert.Equal(10, InvalidationStampContract.Read(Assert.IsType<byte[]>(await cache.GetAsync(second))));
            Assert.Equal(20, InvalidationStampContract.Read(Assert.IsType<byte[]>(cache.Get(first))));
            var ttl = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) };
            cache.Set(first, new byte[] { 1 }, ttl);
            await cache.SetAsync(second, new byte[] { 2 }, ttl);
            cache.Refresh(first);
            await cache.RefreshAsync(second);
            Assert.Equal(new byte[] { 1 }, await cache.GetAsync(first));
            Assert.Equal(new byte[] { 2 }, cache.Get(second));
            cache.Remove(first);
            Assert.Null(await cache.GetAsync(first));
            Assert.Equal(new byte[] { 2 }, await cache.GetAsync(second));
            await cache.RemoveAsync(second);
            Assert.Null(cache.Get(second));
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }
}
