using Microsoft.Data.SqlClient;
using Xunit;

namespace MethodCache.SqlServer.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public class SchemaContractTests(SqlServerFixture fixture)
{
    [SkippableTheory]
    [InlineData("cache-value")]
    [InlineData("backplane-target")]
    [InlineData("version-shape")]
    public async Task VersionOneWithIncompatibleColumns_IsRejected(string shape)
    {
        var options = new SqlServerOptions { ConnectionString = fixture.RequireConnectionString(), SchemaName = "shape_" + Guid.NewGuid().ToString("N") };
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            var mutation = shape switch
            {
                "cache-value" => $"ALTER TABLE {options.QualifiedTableName} ALTER COLUMN [Value] varbinary(max) NULL;",
                "backplane-target" => $"ALTER TABLE {options.QualifiedInvalidationsTableName} ALTER COLUMN [Target] nvarchar(8) NOT NULL;",
                _ => $"ALTER TABLE {options.QualifiedVersionTableName} ALTER COLUMN [Version] nvarchar(8) NOT NULL;"
            };
            await ExecuteAsync(options, mutation);
            var error = await Assert.ThrowsAsync<SqlException>(() => SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None));
            Assert.Equal(50001, error.Number);
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task CreationDisabled_DoesNotRecreateMissingIndexes()
    {
        var options = new SqlServerOptions { ConnectionString = fixture.RequireConnectionString(), SchemaName = "readonly_" + Guid.NewGuid().ToString("N") };
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await ExecuteAsync(options, $"DROP INDEX [Index_Scope_Id] ON {options.QualifiedInvalidationsTableName};");
            options.CreateTablesIfMissing = false;
            var error = await Assert.ThrowsAsync<SqlException>(() => SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None));
            Assert.Equal(50001, error.Number);
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task DeliveredMigration_RejectsAnIncompatibleLegacyLayout()
    {
        var options = new SqlServerOptions { ConnectionString = fixture.RequireConnectionString(), SchemaName = "badmigration_" + Guid.NewGuid().ToString("N") };
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await ExecuteAsync(options, $"DROP TABLE {options.QualifiedVersionTableName}; ALTER TABLE {options.QualifiedTableName} ALTER COLUMN [Value] varbinary(max) NULL;");
            var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "migrations", "0001-version-existing-schema.sql"));
            migration = migration.Replace("DECLARE @SchemaName sysname = N'dbo';", "DECLARE @SchemaName sysname = N'" + options.SchemaName + "';", StringComparison.Ordinal);
            var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(options, migration));
            Assert.Equal(50001, error.Number);
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task ExistingUnversionedSchema_RequiresTheDeliveredExplicitMigration()
    {
        var options = new SqlServerOptions { ConnectionString = fixture.RequireConnectionString(), SchemaName = "migration_" + Guid.NewGuid().ToString("N") };
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await ExecuteAsync(options, $"DROP TABLE {options.QualifiedVersionTableName};");
            var exception = await Assert.ThrowsAsync<SqlException>(() => SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None));
            Assert.Equal(50001, exception.Number);
            Assert.Contains("unversioned", exception.Message, StringComparison.Ordinal);
            var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "migrations", "0001-version-existing-schema.sql"));
            migration = migration.Replace("DECLARE @SchemaName sysname = N'dbo';", "DECLARE @SchemaName sysname = N'" + options.SchemaName + "';", StringComparison.Ordinal);
            await ExecuteAsync(options, migration);
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await ExecuteAsync(options, $"UPDATE {options.QualifiedVersionTableName} SET [Version] = 99;");
            var incompatible = await Assert.ThrowsAsync<SqlException>(() => SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None));
            Assert.Equal(50001, incompatible.Number);
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }

    private static async Task ExecuteAsync(SqlServerOptions options, string sql)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
