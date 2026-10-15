using MethodCache.Extensibility;
using MethodCache.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;

namespace MethodCache.SqlServer.IntegrationTests.FaultInjection;

[Collection(SqlServerCollection.Name)]
public class LockedGapReviewTests(SqlServerFixture fixture)
{
    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PersistentLockedGap_NewerInvalidationsKeepArriving(bool foreignScope, bool commit)
    {
        var options = new SqlServerOptions { ConnectionString = fixture.RequireConnectionString(), SchemaName = "gaplock_" + Guid.NewGuid().ToString("N") };
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
            await using (var insert = new SqlCommand($"INSERT INTO {options.QualifiedInvalidationsTableName} (Scope, InstanceId, MessageType, Target) VALUES (@scope, 'other', 1, '{SqlWireFixture.Key("late")}');", connection, transaction))
            {
                insert.Parameters.AddWithValue("@scope", SqlServerBackplane.GetScope(foreignScope ? "foreign:" : "methodcache:"));
                await insert.ExecuteNonQueryAsync();
            }
            await using var peerConnection = new SqlConnection(options.ConnectionString);
            await peerConnection.OpenAsync();
            await using var peerTransaction = peerConnection.BeginTransaction();
            await using (var peerInsert = new SqlCommand($"INSERT INTO {options.QualifiedInvalidationsTableName} (Scope, InstanceId, MessageType, Target) VALUES (@scope, 'other', 1, '{SqlWireFixture.Key("peer")}');", peerConnection, peerTransaction))
            {
                peerInsert.Parameters.AddWithValue("@scope", SqlServerBackplane.GetScope("methodcache:"));
                await peerInsert.ExecuteNonQueryAsync();
            }
            await publisher.PublishInvalidationAsync("first");
            await PollWithoutHangingAsync(receiver, Handle);
            Assert.Equal(1, receiver.GapRangeCount);
            clock.Advance(TimeSpan.FromSeconds(6));
            // One late gap commits while the earlier transaction stays open. Both this late message and newer
            // messages must arrive even though the probe still cannot retire the earlier gap.
            await peerTransaction.CommitAsync();

            // Keep the same transaction open through multiple polls. A lock/command timeout must not consume
            // the gap, hold newer invalidations hostage, or make those newer messages repeat on the next poll.
            for (var poll = 0; poll < 3; poll++)
            {
                var key = "new-" + poll;
                await publisher.PublishInvalidationAsync(key);
                await PollWithoutHangingAsync(receiver, Handle);
                Assert.Single(delivered, message => message.Key == key);
                Assert.Equal(1, receiver.GapRangeCount);
                clock.Advance(TimeSpan.FromSeconds(1));
            }
            Assert.DoesNotContain(delivered, message => message.Type == BackplaneMessageType.ClearAll);
            Assert.Single(delivered, message => message.Key == "peer");
            if (commit) await transaction.CommitAsync();
            else await transaction.RollbackAsync();
            await PollWithoutHangingAsync(receiver, Handle);
            await PollWithoutHangingAsync(receiver, Handle);
            Assert.Equal(commit && !foreignScope ? 1 : 0, delivered.Count(message => message.Key == "late"));
            Assert.Equal(0, receiver.GapRangeCount);
            Assert.Single(delivered, message => message.Key == "first");
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }

    private static async Task PollWithoutHangingAsync(SqlServerBackplane receiver, Func<BackplaneMessage, Task> handler)
    {
        using var cancellation = new CancellationTokenSource();
        var poll = receiver.PollOnceAsync(handler, cancellation.Token);
        try { await poll.WaitAsync(TimeSpan.FromSeconds(5)); } // Safety bound only; discovery time uses the fake clock.
        finally
        {
            await cancellation.CancelAsync();
            try { await poll; } catch (OperationCanceledException) { }
        }
    }
}
