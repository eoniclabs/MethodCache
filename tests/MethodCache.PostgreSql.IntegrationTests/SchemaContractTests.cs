using MethodCache.Infrastructure;

namespace MethodCache.PostgreSql.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public class SchemaContractTests(PostgreSqlFixture fixture)
{
    [SkippableTheory]
    [InlineData("cache-value")]
    [InlineData("cache-key")]
    [InlineData("backplane-target")]
    [InlineData("cursor-id")]
    [InlineData("version-shape")]
    public async Task VersionOneWithIncompatibleColumns_IsRejected(string shape)
    {
        var options = fixture.Options("shape");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            var mutation = shape switch
            {
                "cache-value" => $"ALTER TABLE {options.QualifiedTableName} ALTER COLUMN value DROP NOT NULL",
                "cache-key" => $"ALTER TABLE {options.QualifiedTableName} ALTER COLUMN id TYPE text",
                "backplane-target" => $"ALTER TABLE {options.QualifiedInvalidationsTableName} ALTER COLUMN target TYPE varchar(8)",
                "cursor-id" => $"ALTER TABLE {options.QualifiedCursorTableName} ALTER COLUMN last_id TYPE integer",
                _ => $"ALTER TABLE {options.QualifiedVersionTableName} ALTER COLUMN version TYPE text"
            };
            await PostgreSqlFixture.ExecuteAsync(options.ConnectionString, mutation);

            await Assert.ThrowsAsync<IncompatibleDistributedScopeException>(() => PostgreSqlTables.CreateIfMissingAsync(connection.DataSource, options, CancellationToken.None));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task UnsupportedVersion_IsRejected()
    {
        var options = fixture.Options("version");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await PostgreSqlFixture.ExecuteAsync(options.ConnectionString, $"UPDATE {options.QualifiedVersionTableName} SET version = 99");

            var error = await Assert.ThrowsAsync<IncompatibleDistributedScopeException>(() => PostgreSqlTables.CreateIfMissingAsync(connection.DataSource, options, CancellationToken.None));
            Assert.Contains("version", error.Message, StringComparison.Ordinal);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task TablesWithoutAVersion_AreRejected()
    {
        var options = fixture.Options("unversioned");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await PostgreSqlFixture.ExecuteAsync(options.ConnectionString, $"DROP TABLE {options.QualifiedVersionTableName}");

            var error = await Assert.ThrowsAsync<IncompatibleDistributedScopeException>(() => PostgreSqlTables.CreateIfMissingAsync(connection.DataSource, options, CancellationToken.None));
            Assert.Contains("no schema version", error.Message, StringComparison.Ordinal);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task MissingTable_IsRecreated_WhenCreationIsAllowed()
    {
        var options = fixture.Options("partial");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await PostgreSqlFixture.ExecuteAsync(options.ConnectionString, $"DROP TABLE {options.QualifiedCursorTableName}");

            await PostgreSqlTables.CreateIfMissingAsync(connection.DataSource, options, CancellationToken.None);

            Assert.Equal(0L, await PostgreSqlFixture.ScalarAsync<long>(options.ConnectionString, $"SELECT count(*) FROM {options.QualifiedCursorTableName}"));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableTheory]
    [InlineData("schema")]
    [InlineData("table")]
    [InlineData("index")]
    public async Task CreationDisabled_DoesNotCreateAnything(string missing)
    {
        var options = fixture.Options("readonly");
        try
        {
            if (missing != "schema")
            {
                await using var created = await CacheTests.CreateAsync(options);
                await PostgreSqlFixture.ExecuteAsync(options.ConnectionString, missing == "table"
                    ? $"DROP TABLE {options.QualifiedInvalidationsTableName}"
                    : $"DROP INDEX {PostgreSqlOptions.Quote(options.SchemaName)}.{PostgreSqlOptions.Quote(options.TableName + "_expires")}");
            }

            options.CreateTablesIfMissing = false;
            await using var connection = new PostgreSqlConnection(options.ConnectionString);
            var error = await Assert.ThrowsAsync<IncompatibleDistributedScopeException>(() => PostgreSqlTables.CreateIfMissingAsync(connection.DataSource, options, CancellationToken.None));
            Assert.Contains("migration", error.Message, StringComparison.Ordinal);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task DeliveredMigration_CreatesTablesMethodCacheAccepts_WithoutCreatingAnything()
    {
        var options = fixture.Options("migration");
        try
        {
            var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "migrations", "0001-create-schema.sql"));
            migration = migration.Replace("\"public\".", PostgreSqlOptions.Quote(options.SchemaName) + ".", StringComparison.Ordinal);
            await PostgreSqlFixture.ExecuteAsync(options.ConnectionString, $"CREATE SCHEMA {PostgreSqlOptions.Quote(options.SchemaName)}");
            await PostgreSqlFixture.ExecuteAsync(options.ConnectionString, migration);

            options.CreateTablesIfMissing = false;
            await using var connection = new PostgreSqlConnection(options.ConnectionString);
            await PostgreSqlTables.CreateIfMissingAsync(connection.DataSource, options, CancellationToken.None);
            var cache = new PostgreSqlDistributedCache(connection, options);
            await cache.SetAsync("key", [1], new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) });
            Assert.Equal(new byte[] { 1 }, await cache.GetAsync("key"));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task ConcurrentInstances_CreateTheTablesOnce()
    {
        var options = fixture.Options("race");
        try
        {
            var connections = Enumerable.Range(0, 6).Select(_ => new PostgreSqlConnection(options.ConnectionString)).ToArray();
            try
            {
                await Task.WhenAll(connections.Select(c => Task.Run(() => PostgreSqlTables.CreateIfMissingAsync(c.DataSource, options, CancellationToken.None))));
            }
            finally
            {
                foreach (var connection in connections) await connection.DisposeAsync();
            }

            Assert.Equal(1L, await PostgreSqlFixture.ScalarAsync<long>(options.ConnectionString, $"SELECT count(*) FROM {options.QualifiedVersionTableName}"));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task CustomNames_WithCapitalsAndQuotes_Work()
    {
        var options = fixture.Options("Names");
        options.TableName = "Cache \"Entries\"";
        options.InvalidationsTableName = "Cache Invalidations";
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            var cache = new PostgreSqlDistributedCache(connection, options);
            await cache.SetAsync("key", [1], new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) });
            await cache.WriteInvalidationStampAsync("stamp", 1, TimeSpan.FromMinutes(1));
            await using var backplane = new PostgreSqlBackplane(connection, options);
            await backplane.PollOnceAsync(_ => Task.CompletedTask, CancellationToken.None);
            await backplane.PublishInvalidationAsync("key");

            Assert.Equal(new byte[] { 1 }, await cache.GetAsync("key"));
            await PostgreSqlTables.CreateIfMissingAsync(connection.DataSource, options, CancellationToken.None);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }
}
