using System.Transactions;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using MethodCache.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace MethodCache.SqlServer.IntegrationTests;

/// <summary>
/// When an instance may have missed invalidations, it must clear its memory; and the table must keep what it hasn't read.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class BackplaneRecoveryTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task OutageFromSubscribingToTheFirstPoll_ClearsMemory()
    {
        // SQL Server was unreachable from startup: values cached meanwhile may have lost invalidations already deleted
        var options = Options("outage_first");
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
            await using var receiver = new SqlServerBackplane(options, timeProvider: time);
            await using var sender = new SqlServerBackplane(options);
            await sender.PublishInvalidationAsync("kept");

            time.Advance(options.BackplaneRetention / 2);
            var delivered = new List<BackplaneMessage>();
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);
            await sender.PublishInvalidationAsync("after");
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);

            Assert.Equal([BackplaneMessageType.ClearAll, BackplaneMessageType.KeyInvalidation], delivered.Select(m => m.Type));
            Assert.Equal("after", delivered[1].Key);
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task FirstPoll_ThatWaitsForAConnection_SkipsNoInvalidationWithoutAClear()
    {
        var options = Options("pool_wait");
        var receiverOptions = Options("pool_wait");
        receiverOptions.SchemaName = options.SchemaName;
        receiverOptions.ConnectionString = new SqlConnectionStringBuilder(options.ConnectionString) { MaxPoolSize = 1 }.ConnectionString;
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await using var sender = new SqlServerBackplane(options);
            // The real clock: the wait for a connection is what must not move where reading starts
            await using var receiver = new SqlServerBackplane(receiverOptions);
            var delivered = new List<BackplaneMessage>();

            Task poll;
            await using (var held = new SqlConnection(receiverOptions.ConnectionString))
            {
                await held.OpenAsync();
                // The first poll starts, then waits for the pool's only connection
                poll = receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);
                await sender.PublishInvalidationAsync("after subscribing");
                await WaitUntilAsync(async () => await ScalarAsync<int>(options, $"""
                    SELECT CASE WHEN MIN(DATEDIFF(millisecond, [CreatedAt], SYSUTCDATETIME())) > 1500 THEN 1 ELSE 0 END
                    FROM {options.QualifiedInvalidationsTableName};
                    """) == 1);
            }

            await poll;
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);

            Assert.Contains(delivered, m => m.Key == "after subscribing" || m.Type == BackplaneMessageType.ClearAll);
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task BacklogUnreadForHalfTheRetention_ClearsMemory()
    {
        // More invalidations than one poll reads: the rest may be deleted before this instance gets to them
        var options = Options("backlog");
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
            await using var receiver = new SqlServerBackplane(options, timeProvider: time);
            var delivered = new List<BackplaneMessage>();
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);
            const int OnePoll = SqlServerBackplane.MaxBatchesPerPoll * SqlServerBackplane.BatchSize;
            await InsertAsync(options, 2 * OnePoll + 1, "burst", TimeSpan.Zero);

            // Each poll comes a quarter of the retention after the last and reads, but none catches up
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);
            time.Advance(options.BackplaneRetention / 4);
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);
            time.Advance(options.BackplaneRetention / 4);
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);

            Assert.Equal(2 * OnePoll, delivered.Count(m => m.Type == BackplaneMessageType.KeyInvalidation));
            Assert.Equal(BackplaneMessageType.ClearAll, Assert.Single(delivered, m => m.Type != BackplaneMessageType.KeyInvalidation).Type);
            Assert.Equal(BackplaneMessageType.ClearAll, delivered[^1].Type);
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task Cleanup_KeepsInvalidations_AsLongAsTheLongestRetentionOfItsPrefix()
    {
        // During a rolling deployment that shortens the retention, an instance that still uses the longer one may be
        // out for longer than the shorter retention: it must still find what it missed, since it won't clear its memory
        var options = Options("retention");
        var shorter = Options("retention");
        shorter.SchemaName = options.SchemaName;
        shorter.BackplaneRetention = TimeSpan.FromMinutes(1);
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await using var longer = new SqlServerBackplane(options);
            await longer.PollOnceAsync(_ => Task.CompletedTask, CancellationToken.None);
            await InsertAsync(options, 10, "within the longer retention", TimeSpan.FromMinutes(10));
            await InsertAsync(options, 10, "past both", TimeSpan.FromHours(2));

            await using var cleaner = new SqlServerBackplane(shorter);
            await cleaner.CleanupOnceAsync(CancellationToken.None);

            Assert.Equal(10, await ScalarAsync<int>(options, $"SELECT COUNT(*) FROM {options.QualifiedInvalidationsTableName};"));
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task Cleanup_RunsOnSubscribing_WithoutHoldingBackDelivery()
    {
        // Deleting a large backlog can take a while; invalidations must keep arriving meanwhile
        var options = Options("cleanup_apart");
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await InsertAsync(options, 10, "old", TimeSpan.FromHours(2));
            var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
            await using var receiver = new SqlServerBackplane(options, timeProvider: time);
            await using var sender = new SqlServerBackplane(options);
            // Where reading starts is known before the deletes are held: that read may not skip rows being deleted
            await receiver.PollOnceAsync(_ => Task.CompletedTask, CancellationToken.None);

            // Every DELETE on the invalidations table waits for a lock this test holds
            await ExecuteAsync(options, $"""
                CREATE TRIGGER {SqlServerOptions.Quote(options.SchemaName)}.[hold] ON {options.QualifiedInvalidationsTableName} AFTER DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    DECLARE @result int;
                    EXEC @result = sp_getapplock @Resource = N'{options.SchemaName}', @LockMode = N'Shared', @LockOwner = N'Transaction', @LockTimeout = -1;
                END
                """);
            await using var holder = new SqlConnection(options.ConnectionString);
            await holder.OpenAsync();
            await ExecuteAsync(holder, options, "EXEC sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Session';");

            var delivered = new List<string>();
            await receiver.SubscribeAsync(message => { lock (delivered) delivered.Add(message.Key!); return Task.CompletedTask; });
            // Its cleanup started when it subscribed, and waits
            await WaitUntilAsync(async () =>
            {
                time.Advance(options.BackplanePollingInterval);
                return await ScalarAsync<int>(options, "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE wait_resource LIKE N'APPLICATION:%';") > 0;
            });

            foreach (var key in new[] { "first", "second" })
            {
                await sender.PublishInvalidationAsync(key);
                await WaitUntilAsync(() =>
                {
                    time.Advance(options.BackplanePollingInterval);
                    lock (delivered) return Task.FromResult(delivered.Contains(key));
                });
            }

            await ExecuteAsync(holder, options, "EXEC sp_releaseapplock @Resource = @resource, @LockOwner = N'Session';");
            await WaitUntilAsync(async () => await ScalarAsync<int>(options,
                $"SELECT COUNT(*) FROM {options.QualifiedInvalidationsTableName} WHERE [CreatedAt] < DATEADD(hour, -1, SYSUTCDATETIME());") == 0);
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task Writes_InsideTheApplicationsTransactionScope_AreNotPartOfIt()
    {
        var schema = "scope_" + Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddMethodCache(mc => mc.UseSqlServer(o =>
        {
            o.ConnectionString = new SqlConnectionStringBuilder(fixture.RequireConnectionString()) { Enlist = true }.ConnectionString;
            o.SchemaName = schema;
        }));
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<SqlServerRuntimeOptions>().Options;
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            var cache = new SqlServerDistributedCache(options);
            await using var backplane = new SqlServerBackplane(options);

            using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            {
                await cache.SetAsync("key", [1], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) });
                await backplane.PublishInvalidationAsync("key");
                // Not completed: the application's transaction rolls back
            }

            Assert.Equal(new byte[] { 1 }, await cache.GetAsync("key"));
            Assert.Equal(1, await ScalarAsync<int>(options, $"SELECT COUNT(*) FROM {options.QualifiedInvalidationsTableName};"));
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }

    private SqlServerOptions Options(string name) => new()
    {
        ConnectionString = fixture.RequireConnectionString(),
        SchemaName = name + "_" + Guid.NewGuid().ToString("N"),
    };

    private static Func<BackplaneMessage, Task> Collect(List<BackplaneMessage> delivered) => message =>
    {
        delivered.Add(message);
        return Task.CompletedTask;
    };

    /// <summary>Inserts <paramref name="count"/> key invalidations from another instance, written <paramref name="age"/> ago.</summary>
    private static Task InsertAsync(SqlServerOptions options, int count, string key, TimeSpan age, string prefix = "methodcache:")
        => ExecuteAsync(options, $"""
            INSERT INTO {options.QualifiedInvalidationsTableName} ([Scope], [InstanceId], [MessageType], [Target], [CreatedAt])
            SELECT TOP (@count) @scope, 'other', @type, @target, DATEADD(second, -@age, SYSUTCDATETIME())
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            """, command =>
        {
            command.Parameters.AddWithValue("@count", count);
            command.Parameters.AddWithValue("@scope", SqlServerBackplane.GetScope(prefix));
            command.Parameters.AddWithValue("@type", (byte)BackplaneWire.Version);
            command.Parameters.AddWithValue("@target", SqlWireFixture.Key(key));
            command.Parameters.AddWithValue("@age", (int)age.TotalSeconds);
        });

    private static async Task ExecuteAsync(SqlServerOptions options, string sql, Action<SqlCommand>? parameters = null)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        parameters?.Invoke(command);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(SqlConnection connection, SqlServerOptions options, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@resource", options.SchemaName);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(SqlServerOptions options, string sql)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not hold within 20 seconds.");
            await Task.Delay(50);
        }
    }
}
