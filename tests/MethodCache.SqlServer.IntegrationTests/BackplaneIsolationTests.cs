using MethodCache.Extensibility;
using MethodCache.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace MethodCache.SqlServer.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public class BackplaneIsolationTests(SqlServerFixture fixture)
{
    [SkippableTheory]
    [InlineData("tenant-a:", "tenant-b:")]
    [InlineData("tenant-a:", "Tenant-a:")]
    [InlineData("tenant-İ:", "tenant-i:")]
    public async Task SharedTable_DifferentPrefixes_ReceiveOnlyTheirOwnMessages(string prefix, string otherPrefix)
    {
        var options = new SqlServerOptions { ConnectionString = fixture.RequireConnectionString(), SchemaName = "scope_" + Guid.NewGuid().ToString("N"), KeyPrefix = prefix };
        var otherOptions = new SqlServerOptions { ConnectionString = options.ConnectionString, SchemaName = options.SchemaName, KeyPrefix = otherPrefix };
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await using var publisher = new SqlServerBackplane(options);
            await using var subscriber = new SqlServerBackplane(options);
            await using var other = new SqlServerBackplane(otherOptions);
            var ownMessages = new List<BackplaneMessage>();
            var otherMessages = new List<BackplaneMessage>();
            Task Own(BackplaneMessage message) { ownMessages.Add(message); return Task.CompletedTask; }
            Task Other(BackplaneMessage message) { otherMessages.Add(message); return Task.CompletedTask; }
            await subscriber.PollOnceAsync(Own, CancellationToken.None);
            await other.PollOnceAsync(Other, CancellationToken.None);

            await publisher.PublishInvalidationAsync("same-key");
            await publisher.PublishTagInvalidationAsync("same-tag");
            await other.PublishInvalidationAsync("other-key");
            await subscriber.PollOnceAsync(Own, CancellationToken.None);
            await other.PollOnceAsync(Other, CancellationToken.None);

            Assert.Equal(2, ownMessages.Count);
            Assert.Equal("same-key", ownMessages[0].Key);
            Assert.Equal("same-tag", ownMessages[1].Tag);
            Assert.Empty(otherMessages);
        }
        finally { await DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task Backplane_PreservesLoneSurrogatesThroughSqlTransport()
    {
        var options = new SqlServerOptions { ConnectionString = fixture.RequireConnectionString(), SchemaName = "wire_" + Guid.NewGuid().ToString("N") };
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await using var receiver = new SqlServerBackplane(options);
            await using var publisher = new SqlServerBackplane(options);
            var delivered = new List<string>();
            Task Handle(BackplaneMessage message) { delivered.Add(message.Tag!); return Task.CompletedTask; }
            await receiver.PollOnceAsync(Handle, CancellationToken.None);
            var tags = new[] { "tag:\ud800", "tag:\ud801", "tag:\udfff", "tag:\0", "tag:\ufffd" };
            foreach (var tag in tags) await publisher.PublishTagInvalidationAsync(tag);
            await receiver.PollOnceAsync(Handle, CancellationToken.None);
            Assert.Equal(tags, delivered);
        }
        finally { await DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task SharedTable_OtherPrefixesRows_AreNotTrackedAsGaps_AndALateOwnRowStillArrives()
    {
        var options = new SqlServerOptions { ConnectionString = fixture.RequireConnectionString(), SchemaName = "scope_" + Guid.NewGuid().ToString("N"), KeyPrefix = "app-b:" };
        var otherOptions = new SqlServerOptions { ConnectionString = options.ConnectionString, SchemaName = options.SchemaName, KeyPrefix = "app-a:" };
        var clock = new FakeTimeProvider();
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await using var receiver = new SqlServerBackplane(options, timeProvider: clock);
            await using var publisher = new SqlServerBackplane(options);
            await using var otherApp = new SqlServerBackplane(otherOptions);
            var delivered = new List<string>();
            Task Handle(BackplaneMessage message) { delivered.Add(message.Key!); return Task.CompletedTask; }
            await receiver.PollOnceAsync(Handle, CancellationToken.None);

            // The other application's rows take the identity values between this application's rows
            for (var i = 0; i < 10; i++)
            {
                await otherApp.PublishInvalidationAsync("other-" + i);
                await otherApp.PublishInvalidationAsync("other-again-" + i);
                await publisher.PublishInvalidationAsync("own-" + i);
            }
            await receiver.PollOnceAsync(Handle, CancellationToken.None);

            Assert.Equal(Enumerable.Range(0, 10).Select(i => "own-" + i), delivered);
            Assert.Equal(0, receiver.GapRangeCount);
            Assert.Equal(0, receiver.GapIdCount);

            // A row of this application that commits after later rows were read is a real gap, and arrives
            await using var connection = new SqlConnection(options.ConnectionString);
            await connection.OpenAsync();
            await using var transaction = connection.BeginTransaction();
            await using (var insert = new SqlCommand($"INSERT INTO {options.QualifiedInvalidationsTableName} (Scope, InstanceId, MessageType, Target) VALUES (@scope, 'other', 4, '4:bABhAHQAZQA=');", connection, transaction))
            {
                insert.Parameters.AddWithValue("@scope", SqlServerBackplane.GetScope(options.KeyPrefix));
                await insert.ExecuteNonQueryAsync();
            }
            for (var i = 0; i < 5; i++)
                await otherApp.PublishInvalidationAsync("later-other-" + i);
            await publisher.PublishInvalidationAsync("after");
            await receiver.PollOnceAsync(Handle, CancellationToken.None);

            Assert.Equal("after", delivered[^1]);
            Assert.Equal(1, receiver.GapRangeCount);
            Assert.Equal(1, receiver.GapIdCount);

            await transaction.CommitAsync();
            await receiver.PollOnceAsync(Handle, CancellationToken.None);
            await receiver.PollOnceAsync(Handle, CancellationToken.None);

            Assert.Equal(new[] { "after", "late" }, delivered.Skip(10));
            Assert.Equal(0, receiver.GapRangeCount);
        }
        finally { await DropSchemaAsync(options); }
    }

    internal static async Task DropSchemaAsync(SqlServerOptions options)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"DROP TABLE IF EXISTS {options.QualifiedInvalidationsTableName}; DROP TABLE IF EXISTS {options.QualifiedTableName}; DROP SCHEMA {SqlServerOptions.Quote(options.SchemaName)};", connection);
        await command.ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task Cleanup_OnlyRemovesExpiredRowsForItsOwnPrefix()
    {
        var options = new SqlServerOptions { ConnectionString = fixture.RequireConnectionString(), SchemaName = "scope_" + Guid.NewGuid().ToString("N"), KeyPrefix = "short:", BackplaneRetention = TimeSpan.FromMinutes(1) };
        var otherOptions = new SqlServerOptions { ConnectionString = options.ConnectionString, SchemaName = options.SchemaName, KeyPrefix = "long:", BackplaneRetention = TimeSpan.FromHours(1) };
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await using var own = new SqlServerBackplane(options);
            await using var other = new SqlServerBackplane(otherOptions);
            await own.PollOnceAsync(_ => Task.CompletedTask, CancellationToken.None);
            await own.PublishInvalidationAsync("own");
            await other.PublishInvalidationAsync("other");
            await using var connection = new SqlConnection(options.ConnectionString);
            await connection.OpenAsync();
            await using (var ageRows = new SqlCommand($"UPDATE {options.QualifiedInvalidationsTableName} SET CreatedAt = DATEADD(minute, -2, SYSUTCDATETIME());", connection))
                await ageRows.ExecuteNonQueryAsync();

            await own.PollOnceAsync(_ => Task.CompletedTask, CancellationToken.None);

            await using var remaining = new SqlCommand($"SELECT Target FROM {options.QualifiedInvalidationsTableName};", connection);
            await using var reader = await remaining.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("4:bwB0AGgAZQByAA==", reader.GetString(0));
            Assert.False(await reader.ReadAsync());
        }
        finally { await DropSchemaAsync(options); }
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("Scope binary(32) NULL,")]
    [InlineData("Scope varbinary(32) NOT NULL,")]
    [InlineData("Scope binary(16) NOT NULL,")]
    public async Task ExistingTable_IncompatibleScope_IsRejectedWithoutChangingRows(string scopeColumn)
    {
        var options = new SqlServerOptions { ConnectionString = fixture.RequireConnectionString(), SchemaName = "scope_" + Guid.NewGuid().ToString("N") };
        try
        {
            await using var connection = new SqlConnection(options.ConnectionString);
            await connection.OpenAsync();
            await using var create = new SqlCommand($"""
                CREATE SCHEMA {SqlServerOptions.Quote(options.SchemaName)};
                """, connection);
            await create.ExecuteNonQueryAsync();
            create.CommandText = $"""
                CREATE TABLE {options.QualifiedInvalidationsTableName} (
                    Id bigint IDENTITY PRIMARY KEY, {scopeColumn} InstanceId varchar(32) NOT NULL,
                    MessageType tinyint NOT NULL, Target nvarchar(max) NOT NULL,
                    CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
                INSERT INTO {options.QualifiedInvalidationsTableName} ({(scopeColumn.Length == 0 ? "" : "Scope,")} InstanceId, MessageType, Target)
                VALUES ({(scopeColumn.Length == 0 ? "" : "0x01,")} 'instance', 0, 'preserved');
                """;
            await create.ExecuteNonQueryAsync();

            var error = await Assert.ThrowsAsync<SqlException>(() => SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None));
            Assert.Equal(50001, error.Number);
            Assert.Contains("non-nullable binary(32) Scope", error.Message);

            // A schema error must also fail startup instead of entering the database-outage retry loop.
            var logger = new SchemaErrorLogger();
            using var tables = new SqlServerTables(options, logger);
            var startupError = await Assert.ThrowsAsync<SqlException>(() => tables.StartAsync(CancellationToken.None));
            Assert.Equal(50001, startupError.Number);
            var log = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Error, log.Level);
            Assert.Same(startupError, log.Error);
            Assert.Contains("table initialization stopped", log.Message);

            await using var rows = new SqlCommand($"SELECT Target FROM {options.QualifiedInvalidationsTableName};", connection);
            Assert.Equal("preserved", await rows.ExecuteScalarAsync());
            await using var columns = new SqlCommand("SELECT COL_LENGTH(@table, 'Scope');", connection);
            columns.Parameters.AddWithValue("@table", options.QualifiedInvalidationsTableName);
            var length = await columns.ExecuteScalarAsync();
            if (scopeColumn.Length == 0)
                Assert.Equal(DBNull.Value, length);
            else
                Assert.Equal(scopeColumn.Contains("16", StringComparison.Ordinal) ? 16 : 32, Convert.ToInt32(length));
        }
        finally { await DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task CurrentSchema_RepeatedInitialization_PreservesMessagesAndIndexes()
    {
        var options = new SqlServerOptions { ConnectionString = fixture.RequireConnectionString(), SchemaName = "scope_" + Guid.NewGuid().ToString("N") };
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await using var receiver = new SqlServerBackplane(options);
            await using var publisher = new SqlServerBackplane(options);
            var messages = new List<BackplaneMessage>();
            Task Handle(BackplaneMessage message) { messages.Add(message); return Task.CompletedTask; }
            await receiver.PollOnceAsync(Handle, CancellationToken.None);
            await publisher.PublishInvalidationAsync("scoped");

            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await receiver.PollOnceAsync(Handle, CancellationToken.None);
            Assert.Equal("scoped", Assert.Single(messages).Key);

            await using var connection = new SqlConnection(options.ConnectionString);
            await connection.OpenAsync();
            await using var indexes = new SqlCommand("SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(@table) AND name IN ('Index_Scope_Id', 'Index_Scope_CreatedAt');", connection);
            indexes.Parameters.AddWithValue("@table", options.QualifiedInvalidationsTableName);
            Assert.Equal(2, await indexes.ExecuteScalarAsync());
        }
        finally { await DropSchemaAsync(options); }
    }

    private sealed class SchemaErrorLogger : ILogger<SqlServerTables>
    {
        public List<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }

}
