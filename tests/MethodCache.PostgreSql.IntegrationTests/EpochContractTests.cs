using Npgsql;

namespace MethodCache.PostgreSql.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public class EpochContractTests(PostgreSqlFixture fixture)
{
    [SkippableFact]
    public async Task EnsureEpochs_ReorderedAndDuplicateKeys_RetainCallerPositions()
    {
        var options = fixture.Options("epoch_order");
        try
        {
            await using var connection = new PostgreSqlConnection(options.ConnectionString);
            var cache = new PostgreSqlDistributedCache(connection, options);
            var first = await cache.EnsureEpochsAsync(["z", "a", "m"]);
            var advanced = await cache.AdvanceEpochAsync("a");
            var reordered = await cache.EnsureEpochsAsync(["m", "a", "z"]);
            Assert.Equal(new[] { first[2], advanced, first[0] }, reordered);
            var duplicates = await cache.EnsureEpochsAsync(["a", "z", "a", "m"]);
            Assert.Equal(new[] { advanced, first[0], advanced, first[2] }, duplicates);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task EnsureEpochs_FiniteMetadata_IsMadePermanentBeforeReadOnlyReuse()
    {
        var options = fixture.Options("epoch_retention");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            var cache = new PostgreSqlDistributedCache(connection, options);
            string[] keys = ["epoch"];
            var first = await cache.EnsureEpochsAsync(keys);
            await PostgreSqlFixture.ExecuteAsync(options.ConnectionString, $"UPDATE {options.QualifiedTableName} SET expires_at = now() + interval '1 day'");

            Assert.Equal(first, await cache.EnsureEpochsAsync(keys));
            Assert.Equal("infinity", await PostgreSqlFixture.ScalarAsync<string>(options.ConnectionString, $"SELECT expires_at::text FROM {options.QualifiedTableName}"));

            await RejectUpdatesAsync(options);
            Assert.Equal(first, await cache.EnsureEpochsAsync(keys));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task EnsureEpochs_InitializedVector_DoesNotUpdateStorage()
    {
        var options = fixture.Options("epoch_reads");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            var cache = new PostgreSqlDistributedCache(connection, options);
            string[] keys = ["epoch:a", "epoch:b"];
            var first = await cache.EnsureEpochsAsync(keys);
            await RejectUpdatesAsync(options);

            Assert.Equal(first, await cache.EnsureEpochsAsync(keys));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task EnsureEpochs_InitializedVector_DoesNotWaitForAnotherWritersLock()
    {
        var options = fixture.Options("epoch_concurrent");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            var cache = new PostgreSqlDistributedCache(connection, options);
            string[] keys = ["epoch:a", "epoch:b"];
            var first = await cache.EnsureEpochsAsync(keys);
            await using var holder = new NpgsqlConnection(options.ConnectionString);
            await holder.OpenAsync();
            await using var transaction = await holder.BeginTransactionAsync();
            await using (var hold = new NpgsqlCommand($"SELECT value FROM {options.QualifiedTableName} FOR UPDATE", holder, transaction))
                await hold.ExecuteNonQueryAsync();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.Equal(first, await cache.EnsureEpochsAsync(keys, timeout.Token));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task NativeAtomicEpochs_ConformAcrossIndependentInstances()
    {
        var options = fixture.Options("epochs");
        try
        {
            await using var first = await CacheTests.CreateAsync(options);
            await using var second = new PostgreSqlConnection(options.ConnectionString);
            await MethodCache.TestSupport.EpochBackendContract.RunAsync(
                new PostgreSqlDistributedCache(first, options), new PostgreSqlDistributedCache(second, options), "epochs:");
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task RetentionContract_FirstWins_AndAShorterOneIsRejected()
    {
        var options = fixture.Options("contract");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            var cache = new PostgreSqlDistributedCache(connection, options);

            Assert.Equal(TimeSpan.FromDays(7), await cache.EnsureRetentionContractAsync("contract", TimeSpan.FromDays(7)));
            Assert.Equal(TimeSpan.FromDays(7), await cache.EnsureRetentionContractAsync("contract", TimeSpan.FromDays(30)));
            await Assert.ThrowsAsync<MethodCache.Infrastructure.IncompatibleDistributedScopeException>(
                () => cache.EnsureRetentionContractAsync("contract", TimeSpan.FromDays(1)).AsTask());
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    private static Task RejectUpdatesAsync(PostgreSqlOptions options) => PostgreSqlFixture.ExecuteAsync(options.ConnectionString, $"""
        CREATE FUNCTION {PostgreSqlOptions.Quote(options.SchemaName)}.reject_updates() RETURNS trigger LANGUAGE plpgsql AS
        $$ BEGIN RAISE EXCEPTION 'Reading initialized epochs must not update their storage.'; END $$;
        CREATE TRIGGER reject_updates BEFORE UPDATE ON {options.QualifiedTableName}
        FOR EACH ROW EXECUTE FUNCTION {PostgreSqlOptions.Quote(options.SchemaName)}.reject_updates();
        """);
}
