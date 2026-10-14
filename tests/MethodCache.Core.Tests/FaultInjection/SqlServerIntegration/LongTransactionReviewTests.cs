using MethodCache.Extensibility;
using MethodCache.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;

namespace MethodCache.SqlServer.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public class LongTransactionReviewTests(SqlServerFixture fixture)
{
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Backplane_LateTransactionBeyondGapLifetime_ResolvesWithoutLosingInvalidation(bool commit)
    {
        var options = new SqlServerOptions { ConnectionString = fixture.RequireConnectionString(), SchemaName = "review_" + Guid.NewGuid().ToString("N") };
        var clock = new FakeTimeProvider();
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await using var receiver = new SqlServerBackplane(options, timeProvider: clock);
            await using var publisher = new SqlServerBackplane(options);
            var delivered = new List<BackplaneMessage>();
            Task Handle(BackplaneMessage message) { delivered.Add(message); return Task.CompletedTask; }
            await receiver.PollOnceAsync(Handle, CancellationToken.None);

            await using var connection = new SqlConnection(options.ConnectionString);
            await connection.OpenAsync();
            await using var transaction = connection.BeginTransaction();
            await using (var insert = new SqlCommand($"INSERT INTO {options.QualifiedInvalidationsTableName} (Scope, InstanceId, MessageType, Target) VALUES (@scope, 'other', 4, '4:bABhAHQAZQA=');", connection, transaction))
            {
                insert.Parameters.AddWithValue("@scope", SqlServerBackplane.GetScope(options.KeyPrefix));
                await insert.ExecuteNonQueryAsync();
            }
            await publisher.PublishInvalidationAsync("after");
            await receiver.PollOnceAsync(Handle, CancellationToken.None);
            Assert.Equal(1, receiver.GapRangeCount);

            clock.Advance(TimeSpan.FromSeconds(6));
            var latePoll = receiver.PollOnceAsync(Handle, CancellationToken.None);
            if (commit) await transaction.CommitAsync();
            else await transaction.RollbackAsync();
            await latePoll;
            await receiver.PollOnceAsync(Handle, CancellationToken.None);

            Assert.Equal(commit, delivered.Any(message => message.Key == "late"));
            Assert.Equal(0, receiver.GapRangeCount);
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }
}
