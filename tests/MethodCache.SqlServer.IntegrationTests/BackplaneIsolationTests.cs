using MethodCache.Extensibility;
using MethodCache.SqlServer;
using Microsoft.Data.SqlClient;

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
