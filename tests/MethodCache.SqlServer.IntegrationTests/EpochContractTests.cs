using Microsoft.Data.SqlClient;
using Xunit;

namespace MethodCache.SqlServer.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public class EpochContractTests(SqlServerFixture fixture)
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureEpochs_FiniteOrSlidingMetadata_NormalizesRetentionBeforeReadOnlyReuse(bool sliding)
    {
        var options = new SqlServerOptions
        { ConnectionString = fixture.RequireConnectionString(), SchemaName = "epoch_retention_" + Guid.NewGuid().ToString("N") };
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            var cache = new SqlServerDistributedCache(options);
            string[] keys = ["epoch"];
            var first = await cache.EnsureEpochsAsync(keys);
            await using var connection = new SqlConnection(options.ConnectionString);
            await connection.OpenAsync();
            await using (var shorten = new SqlCommand($"UPDATE {options.QualifiedTableName} SET ExpiresAtTime = @finite, AbsoluteExpiration = @finite, SlidingExpirationInSeconds = @sliding;", connection))
            {
                shorten.Parameters.AddWithValue("@finite", DateTimeOffset.MaxValue.AddDays(-1));
                shorten.Parameters.AddWithValue("@sliding", sliding ? 60L : DBNull.Value);
                await shorten.ExecuteNonQueryAsync();
            }

            Assert.Equal(first, await cache.EnsureEpochsAsync(keys));
            await using (var inspect = new SqlCommand($"SELECT ExpiresAtTime, AbsoluteExpiration, SlidingExpirationInSeconds FROM {options.QualifiedTableName};", connection))
            await using (var reader = await inspect.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(DateTimeOffset.MaxValue, reader.GetFieldValue<DateTimeOffset>(0));
                Assert.Equal(DateTimeOffset.MaxValue, reader.GetFieldValue<DateTimeOffset>(1));
                Assert.True(reader.IsDBNull(2));
            }

            await using var rejectUpdates = new SqlCommand($"""
                CREATE TRIGGER [{options.SchemaName}].[RejectEpochReadUpdates]
                ON {options.QualifiedTableName} AFTER UPDATE AS
                THROW 51000, 'Reading initialized epochs must not update their storage.', 1;
                """, connection);
            await rejectUpdates.ExecuteNonQueryAsync();
            Assert.Equal(first, await cache.EnsureEpochsAsync(keys));
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task EnsureEpochs_InitializedVector_DoesNotUpdateStorage()
    {
        var options = new SqlServerOptions
        { ConnectionString = fixture.RequireConnectionString(), SchemaName = "epoch_reads_" + Guid.NewGuid().ToString("N") };
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            var cache = new SqlServerDistributedCache(options);
            string[] keys = ["epoch:a", "epoch:b"];
            var first = await cache.EnsureEpochsAsync(keys);
            await using var connection = new SqlConnection(options.ConnectionString);
            await connection.OpenAsync();
            await using var rejectUpdates = new SqlCommand($"""
                CREATE TRIGGER [{options.SchemaName}].[RejectEpochReadUpdates]
                ON {options.QualifiedTableName} AFTER UPDATE AS
                THROW 51000, 'Reading initialized epochs must not update their storage.', 1;
                """, connection);
            await rejectUpdates.ExecuteNonQueryAsync();

            Assert.Equal(first, await cache.EnsureEpochsAsync(keys));
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task NativeAtomicEpochs_ConformAcrossIndependentInstances()
    {
        var options = new SqlServerOptions
        { ConnectionString = fixture.RequireConnectionString(), SchemaName = "epochs_" + Guid.NewGuid().ToString("N") };
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await MethodCache.TestSupport.EpochBackendContract.RunAsync(new SqlServerDistributedCache(options), new SqlServerDistributedCache(options), "epochs:");
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }
}
