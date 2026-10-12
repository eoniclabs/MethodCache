using MethodCache.Extensibility;
using MethodCache.SqlServer;
using Microsoft.Data.SqlClient;
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
            await using (var insert = new SqlCommand($"INSERT INTO {options.QualifiedInvalidationsTableName} (Scope, InstanceId, MessageType, Target) VALUES (@scope, 'other', 0, 'late');", connection, transaction))
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
            Assert.Equal("other", reader.GetString(0));
            Assert.False(await reader.ReadAsync());
        }
        finally { await DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task Cleanup_RemovesExpiredLegacyRowsWithoutAScope()
    {
        var options = new SqlServerOptions { ConnectionString = fixture.RequireConnectionString(), SchemaName = "scope_" + Guid.NewGuid().ToString("N") };
        try
        {
            await using var connection = new SqlConnection(options.ConnectionString);
            await connection.OpenAsync();
            await using (var legacy = new SqlCommand($"CREATE SCHEMA {SqlServerOptions.Quote(options.SchemaName)};", connection))
            {
                await legacy.ExecuteNonQueryAsync();
                legacy.CommandText = $"""
                    CREATE TABLE {options.QualifiedInvalidationsTableName} (
                        Id bigint IDENTITY PRIMARY KEY, InstanceId varchar(32) NOT NULL,
                        MessageType tinyint NOT NULL, Target nvarchar(max) NOT NULL,
                        CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
                    INSERT INTO {options.QualifiedInvalidationsTableName} (InstanceId, MessageType, Target, CreatedAt)
                    VALUES ('legacy', 0, 'expired', DATEADD(hour, -2, SYSUTCDATETIME())), ('legacy', 0, 'recent', SYSUTCDATETIME());
                    """;
                await legacy.ExecuteNonQueryAsync();
            }
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await using var backplane = new SqlServerBackplane(options);

            await backplane.PollOnceAsync(_ => Task.CompletedTask, CancellationToken.None);
            await backplane.PollOnceAsync(_ => Task.CompletedTask, CancellationToken.None);

            await using var remaining = new SqlCommand($"SELECT Target FROM {options.QualifiedInvalidationsTableName};", connection);
            Assert.Equal("recent", await remaining.ExecuteScalarAsync());
        }
        finally { await DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task ExistingUnscopedTable_IsUpgradedIdempotentlyAndLegacyMessagesAreIgnored()
    {
        var options = new SqlServerOptions { ConnectionString = fixture.RequireConnectionString(), SchemaName = "scope_" + Guid.NewGuid().ToString("N") };
        try
        {
            await using (var connection = new SqlConnection(options.ConnectionString))
            {
                await connection.OpenAsync();
                await using var legacy = new SqlCommand($"""
                    CREATE SCHEMA {SqlServerOptions.Quote(options.SchemaName)};
                    """, connection);
                await legacy.ExecuteNonQueryAsync();
                legacy.CommandText = $"""
                    CREATE TABLE {options.QualifiedInvalidationsTableName} (
                        Id bigint IDENTITY PRIMARY KEY, InstanceId varchar(32) NOT NULL,
                        MessageType tinyint NOT NULL, Target nvarchar(max) NOT NULL,
                        CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
                    INSERT INTO {options.QualifiedInvalidationsTableName} (InstanceId, MessageType, Target)
                    VALUES ('legacy', 0, 'ambiguous');
                    """;
                await legacy.ExecuteNonQueryAsync();
            }
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await using var receiver = new SqlServerBackplane(options);
            await using var publisher = new SqlServerBackplane(options);
            var messages = new List<BackplaneMessage>();
            Task Handle(BackplaneMessage message) { messages.Add(message); return Task.CompletedTask; }
            await receiver.PollOnceAsync(Handle, CancellationToken.None);
            await publisher.PublishInvalidationAsync("scoped");
            await receiver.PollOnceAsync(Handle, CancellationToken.None);
            Assert.Equal("scoped", Assert.Single(messages).Key);
        }
        finally { await DropSchemaAsync(options); }
    }
}
