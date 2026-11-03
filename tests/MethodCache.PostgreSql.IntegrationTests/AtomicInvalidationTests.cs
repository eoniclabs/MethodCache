using MethodCache.TestSupport;

namespace MethodCache.PostgreSql.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public class AtomicInvalidationTests(PostgreSqlFixture fixture)
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentCacheInstances_PreserveSignedMaximumAndConcurrentStamps(bool concurrent)
    {
        var options = fixture.Options("stamp");
        try
        {
            await using var firstConnection = await CacheTests.CreateAsync(options);
            await using var secondConnection = new PostgreSqlConnection(options.ConnectionString);
            var first = new PostgreSqlDistributedCache(firstConnection, options);
            var second = new PostgreSqlDistributedCache(secondConnection, options);
            if (concurrent) await InvalidationStampContract.AssertConcurrentMaximumAsync(first, second, "stamp");
            else await InvalidationStampContract.AssertMaximumAsync(first, second, "stamp");
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task OlderStampWithShortRetention_DoesNotShortenNewerRetention()
    {
        var options = fixture.Options("retention");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            var cache = new PostgreSqlDistributedCache(connection, options);
            var expires = $"SELECT expires_at FROM {options.QualifiedTableName}";
            await cache.WriteInvalidationStampAsync("stamp", 20, TimeSpan.FromDays(1));
            var before = await PostgreSqlFixture.ScalarAsync<DateTime>(options.ConnectionString, expires);

            await cache.WriteInvalidationStampAsync("stamp", 10, TimeSpan.FromMinutes(1));

            Assert.Equal(before, await PostgreSqlFixture.ScalarAsync<DateTime>(options.ConnectionString, expires));
            Assert.Equal(20, InvalidationStampContract.Read(Assert.IsType<byte[]>(await cache.GetAsync("stamp"))));

            // An expired stamp is history: an older one replaces it
            await PostgreSqlFixture.ExecuteAsync(options.ConnectionString, $"UPDATE {options.QualifiedTableName} SET expires_at = '2000-01-01'");
            await cache.WriteInvalidationStampAsync("stamp", 5, TimeSpan.FromMinutes(1));
            Assert.Equal(5, InvalidationStampContract.Read(Assert.IsType<byte[]>(await cache.GetAsync("stamp"))));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task RetentionBeyondWhatDotNetRepresents_KeepsTheStampForever()
    {
        var options = fixture.Options("forever");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            var cache = new PostgreSqlDistributedCache(connection, options);

            await cache.WriteInvalidationStampAsync("stamp", 1, TimeSpan.MaxValue);

            Assert.Equal("infinity", await PostgreSqlFixture.ScalarAsync<string>(options.ConnectionString, $"SELECT expires_at::text FROM {options.QualifiedTableName}"));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task FirstStamp_CreatesMissingTables()
    {
        // The stamp must not be lost because the tables were not created yet
        var options = fixture.Options("lazy");
        try
        {
            await using var connection = new PostgreSqlConnection(options.ConnectionString);
            var cache = new PostgreSqlDistributedCache(connection, options);

            await cache.WriteInvalidationStampAsync("stamp", 3, TimeSpan.FromMinutes(1));

            Assert.Equal(3, InvalidationStampContract.Read(Assert.IsType<byte[]>(await cache.GetAsync("stamp"))));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task StampOverAValueOfAnotherShape_IsRejectedAsIncompatible()
    {
        var options = fixture.Options("shape");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            var cache = new PostgreSqlDistributedCache(connection, options);
            await cache.SetAsync("stamp", [1, 2, 3], new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) });

            await Assert.ThrowsAsync<MethodCache.Infrastructure.IncompatibleDistributedScopeException>(
                () => cache.WriteInvalidationStampAsync("stamp", 1, TimeSpan.FromMinutes(1)));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }
}
