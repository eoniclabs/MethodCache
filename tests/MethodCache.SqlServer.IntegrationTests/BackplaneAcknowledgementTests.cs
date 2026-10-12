using MethodCache.Extensibility;
using MethodCache.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;
using System.Reflection;

namespace MethodCache.SqlServer.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public class BackplaneAcknowledgementTests(SqlServerFixture fixture)
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSubscriber_RetriesFailedAndRemainingMessages(bool cancellation)
    {
        var options = new SqlServerOptions { ConnectionString = fixture.RequireConnectionString(), SchemaName = "ack_" + Guid.NewGuid().ToString("N") };
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await using var receiver = new SqlServerBackplane(options);
            await using var publisher = new SqlServerBackplane(options);
            var delivered = new List<string>();
            var failed = false;
            Task Handle(BackplaneMessage message)
            {
                if (message.Key == "second" && !failed)
                {
                    failed = true;
                    return Task.FromException(cancellation ? new OperationCanceledException() : new InvalidOperationException("callback failed"));
                }
                delivered.Add(message.Key!);
                return Task.CompletedTask;
            }
            await receiver.PollOnceAsync(Handle, CancellationToken.None);
            foreach (var key in new[] { "first", "second", "third" }) await publisher.PublishInvalidationAsync(key);
            await Assert.ThrowsAnyAsync<Exception>(() => receiver.PollOnceAsync(Handle, CancellationToken.None));
            Assert.Equal(new[] { "first" }, delivered);

            await receiver.PollOnceAsync(Handle, CancellationToken.None);
            await receiver.PollOnceAsync(Handle, CancellationToken.None);

            Assert.Equal(new[] { "first", "second", "third" }, delivered);
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task PoisonMessage_IsReplacedByAClearAfterAFewPolls_AndLaterMessagesArrive()
    {
        var options = new SqlServerOptions { ConnectionString = fixture.RequireConnectionString(), SchemaName = "ack_" + Guid.NewGuid().ToString("N") };
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await using var receiver = new SqlServerBackplane(options);
            await using var publisher = new SqlServerBackplane(options);
            var delivered = new List<string>();
            var poisonAttempts = 0;
            Task Handle(BackplaneMessage message)
            {
                if (message.Key == "poison")
                {
                    poisonAttempts++;
                    throw new InvalidOperationException("this message always fails");
                }
                delivered.Add(message.Type == BackplaneMessageType.ClearAll ? "clear" : message.Key!);
                return Task.CompletedTask;
            }
            await receiver.PollOnceAsync(Handle, CancellationToken.None);
            await publisher.PublishInvalidationAsync("poison");
            await publisher.PublishInvalidationAsync("good");

            // A handful of polls, far fewer than the retention-based outage clear would need
            for (var poll = 0; poll < 5 && !delivered.Contains("good"); poll++)
            {
                try { await receiver.PollOnceAsync(Handle, CancellationToken.None); }
                catch (InvalidOperationException) { }
            }

            // The skipped invalidation is replaced by a clear of this node's memory, so nothing stale stays behind
            Assert.Equal(new[] { "clear", "good" }, delivered);
            Assert.Equal(5, poisonAttempts);
            await receiver.PollOnceAsync(Handle, CancellationToken.None);
            Assert.Equal(5, poisonAttempts);
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task FailedGapMessage_RemainsPendingAfterGapDiscoveryWindowExpires()
    {
        var options = new SqlServerOptions { ConnectionString = fixture.RequireConnectionString(), SchemaName = "ack_" + Guid.NewGuid().ToString("N") };
        var clock = new FakeTimeProvider();
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await using var receiver = new SqlServerBackplane(options, timeProvider: clock);
            var delivered = new List<string>();
            var failed = false;
            Task Handle(BackplaneMessage message)
            {
                if (message.Key == "late" && !failed) { failed = true; throw new InvalidOperationException("callback failed"); }
                delivered.Add(message.Key!);
                return Task.CompletedTask;
            }
            await receiver.PollOnceAsync(Handle, CancellationToken.None);
            await InsertAsync(options, 2, "visible");
            await receiver.PollOnceAsync(Handle, CancellationToken.None);
            await InsertAsync(options, 1, "late");
            await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.PollOnceAsync(Handle, CancellationToken.None));
            clock.Advance(TimeSpan.FromSeconds(10));

            await receiver.PollOnceAsync(Handle, CancellationToken.None);
            await receiver.PollOnceAsync(Handle, CancellationToken.None);

            Assert.Equal(new[] { "visible", "late" }, delivered);
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task FailedOutageClear_DoesNotAdvanceTheRecoveryWatermark()
    {
        var options = new SqlServerOptions { ConnectionString = fixture.RequireConnectionString(), SchemaName = "ack_" + Guid.NewGuid().ToString("N"), BackplaneRetention = TimeSpan.FromMinutes(1) };
        var clock = new FakeTimeProvider();
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await using var receiver = new SqlServerBackplane(options, timeProvider: clock);
            await receiver.PollOnceAsync(_ => Task.CompletedTask, CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(31));
            var attempts = 0;
            Task Clear(BackplaneMessage message)
            {
                Assert.Equal(BackplaneMessageType.ClearAll, message.Type);
                if (++attempts == 1) throw new InvalidOperationException("clear failed");
                return Task.CompletedTask;
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.PollOnceAsync(Clear, CancellationToken.None));

            await receiver.PollOnceAsync(Clear, CancellationToken.None);
            await receiver.PollOnceAsync(Clear, CancellationToken.None);

            Assert.Equal(2, attempts);
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }

    private static async Task InsertAsync(SqlServerOptions options, long id, string key)
    {
        // Explicit identity values reproduce out-of-order visibility without sleeps or a blocked transaction.
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"""
            SET IDENTITY_INSERT {options.QualifiedInvalidationsTableName} ON;
            INSERT INTO {options.QualifiedInvalidationsTableName} (Id, Scope, InstanceId, MessageType, Target)
            VALUES (@id, @scope, 'other', 0, @key);
            SET IDENTITY_INSERT {options.QualifiedInvalidationsTableName} OFF;
            """, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@scope", SqlServerBackplane.GetScope(options.KeyPrefix));
        command.Parameters.AddWithValue("@key", key);
        await command.ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task FailureInSecondBatch_PreservesFirstBatchAndResumesRemainingRows()
    {
        var options = new SqlServerOptions { ConnectionString = fixture.RequireConnectionString(), SchemaName = "ack_" + Guid.NewGuid().ToString("N") };
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await using var receiver = new SqlServerBackplane(options);
            var delivered = new List<string>();
            var failed = false;
            Task Handle(BackplaneMessage message)
            {
                if (message.Key == "1002" && !failed) { failed = true; throw new InvalidOperationException("second batch failed"); }
                delivered.Add(message.Key!);
                return Task.CompletedTask;
            }
            await receiver.PollOnceAsync(Handle, CancellationToken.None);
            await using (var connection = new SqlConnection(options.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand($"""
                    WITH Numbers AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM Numbers WHERE n < 1005)
                    INSERT INTO {options.QualifiedInvalidationsTableName} (Scope, InstanceId, MessageType, Target)
                    SELECT @scope, 'other', 0, CONVERT(nvarchar(10), n) FROM Numbers ORDER BY n
                    OPTION (MAXRECURSION 0);
                    """, connection);
                command.Parameters.AddWithValue("@scope", SqlServerBackplane.GetScope(options.KeyPrefix));
                await command.ExecuteNonQueryAsync();
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.PollOnceAsync(Handle, CancellationToken.None));
            Assert.Equal(1001, delivered.Count);

            await receiver.PollOnceAsync(Handle, CancellationToken.None);
            await receiver.PollOnceAsync(Handle, CancellationToken.None);

            Assert.Equal(Enumerable.Range(1, 1005).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), delivered);
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task CallbackCancellation_DoesNotStopThePollingLoop()
    {
        var options = new SqlServerOptions { ConnectionString = fixture.RequireConnectionString(), SchemaName = "ack_" + Guid.NewGuid().ToString("N"), BackplanePollingInterval = TimeSpan.FromSeconds(1) };
        var clock = new FakeTimeProvider();
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await using var receiver = new SqlServerBackplane(options, timeProvider: clock);
            await using var publisher = new SqlServerBackplane(options);
            await receiver.PollOnceAsync(_ => Task.CompletedTask, CancellationToken.None);
            await publisher.PublishInvalidationAsync("retry");
            var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = 0;
            await receiver.SubscribeAsync(_ =>
            {
                if (++attempts == 1)
                {
                    firstAttempt.TrySetResult();
                    throw new OperationCanceledException("subscriber cancelled its own work");
                }
                delivered.TrySetResult();
                return Task.CompletedTask;
            });
            // Observe premature loop completion too, so a broken implementation fails without a time-based wait.
            var loop = Assert.IsAssignableFrom<Task>(typeof(SqlServerBackplane).GetField("_loop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(receiver));
            clock.Advance(TimeSpan.FromSeconds(1));
            await firstAttempt.Task;
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Same(delivered.Task, await Task.WhenAny(delivered.Task, loop));
            await receiver.UnsubscribeAsync();
            Assert.Equal(2, attempts);
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }
}
