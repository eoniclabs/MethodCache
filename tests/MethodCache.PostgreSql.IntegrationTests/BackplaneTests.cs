using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using NpgsqlTypes;

namespace MethodCache.PostgreSql.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public class BackplaneTests(PostgreSqlFixture fixture)
{
    private static readonly TimeSpan Eventually = TimeSpan.FromSeconds(15);

    [SkippableTheory]
    [InlineData("tenant-a:", "tenant-b:")]
    [InlineData("tenant-a:", "Tenant-a:")]
    public async Task SharedTable_DifferentPrefixes_ReceiveOnlyTheirOwnMessages(string prefix, string otherPrefix)
    {
        var options = fixture.Options("scope");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await using var publisher = new PostgreSqlBackplane(connection, options, prefix: prefix);
            await using var subscriber = new PostgreSqlBackplane(connection, options, prefix: prefix);
            await using var other = new PostgreSqlBackplane(connection, options, prefix: otherPrefix);
            var own = new List<BackplaneMessage>();
            var others = new List<BackplaneMessage>();
            await subscriber.PollOnceAsync(Collect(own), CancellationToken.None);
            await other.PollOnceAsync(Collect(others), CancellationToken.None);

            await publisher.PublishInvalidationAsync("same-key");
            await publisher.PublishTagInvalidationAsync("same-tag");
            await other.PublishInvalidationAsync("other-key");
            await subscriber.PollOnceAsync(Collect(own), CancellationToken.None);
            await other.PollOnceAsync(Collect(others), CancellationToken.None);

            Assert.Equal(2, own.Count);
            Assert.Equal("same-key", own[0].Key);
            Assert.Equal("same-tag", own[1].Tag);
            Assert.Empty(others);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task EveryMessageType_ArrivesExactly_IncludingLoneSurrogates()
    {
        var options = fixture.Options("wire");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await using var receiver = new PostgreSqlBackplane(connection, options);
            await using var publisher = new PostgreSqlBackplane(connection, options);
            var delivered = new List<BackplaneMessage>();
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);
            var tags = new[] { "tag:\ud800", "tag:\udfff", "tag:\0", "tag:�" };

            foreach (var tag in tags) await publisher.PublishTagInvalidationAsync(tag);
            await publisher.PublishInvalidationAsync("key:\ud801");
            await publisher.PublishMethodInvalidationAsync("method");
            await publisher.PublishClearAsync();
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);

            Assert.Equal(tags, delivered.Take(4).Select(m => m.Tag));
            Assert.Equal((BackplaneMessageType.KeyInvalidation, "key:\ud801"), (delivered[4].Type, delivered[4].Key));
            Assert.Equal((BackplaneMessageType.MethodInvalidation, "method"), (delivered[5].Type, delivered[5].Key));
            Assert.Equal(BackplaneMessageType.ClearAll, delivered[6].Type);
            Assert.All(delivered, m => Assert.Equal(publisher.InstanceId, m.InstanceId));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task OwnInvalidations_AreNotDeliveredBack()
    {
        var options = fixture.Options("own");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await using var backplane = new PostgreSqlBackplane(connection, options);
            var delivered = new List<BackplaneMessage>();
            await backplane.PollOnceAsync(Collect(delivered), CancellationToken.None);

            await backplane.PublishInvalidationAsync("key");

            Assert.False(await backplane.PollOnceAsync(Collect(delivered), CancellationToken.None));
            Assert.Empty(delivered);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task PollingState_TracksDeliveryFailureAndRecovery()
    {
        var options = fixture.Options("health");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await using var receiver = new PostgreSqlBackplane(connection, options);
            await using var sender = new PostgreSqlBackplane(connection, options);
            Assert.Equal(BackplaneState.Connecting, receiver.ConnectionState);
            await receiver.PollOnceAsync(_ => Task.CompletedTask, CancellationToken.None);
            Assert.Equal(BackplaneState.Connected, receiver.ConnectionState);
            await sender.PublishInvalidationAsync("key");

            await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.PollOnceAsync(_ => throw new InvalidOperationException("delivery failed"), CancellationToken.None));
            Assert.Equal(BackplaneState.Disconnected, receiver.ConnectionState);
            var delivered = new List<BackplaneMessage>();
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);
            Assert.Equal("key", Assert.Single(delivered).Key);
            Assert.Equal(BackplaneState.Connected, receiver.ConnectionState);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task UnreachableDatabase_ReportsDisconnected()
    {
        var options = new PostgreSqlOptions { ConnectionString = "Host=127.0.0.1;Port=1;Timeout=1" };
        await using var connection = new PostgreSqlConnection(options.ConnectionString);
        await using var backplane = new PostgreSqlBackplane(connection, options);

        await Assert.ThrowsAnyAsync<Exception>(() => backplane.PollOnceAsync(_ => Task.CompletedTask, CancellationToken.None));

        Assert.Equal(BackplaneState.Disconnected, backplane.ConnectionState);
    }

    [SkippableFact]
    public async Task AMessageThatKeepsFailing_IsReplacedByAClearAfterFiveAttempts()
    {
        var options = fixture.Options("poison");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await using var receiver = new PostgreSqlBackplane(connection, options);
            await using var sender = new PostgreSqlBackplane(connection, options);
            await receiver.PollOnceAsync(_ => Task.CompletedTask, CancellationToken.None);
            await sender.PublishInvalidationAsync("poison");
            await sender.PublishInvalidationAsync("next");
            var delivered = new List<BackplaneMessage>();
            Task Handle(BackplaneMessage message) => message.Key == "poison" ? throw new InvalidOperationException("handler failed") : Collect(delivered)(message);

            for (var attempt = 1; attempt < PostgreSqlBackplane.MaxDeliveryAttempts; attempt++)
                await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.PollOnceAsync(Handle, CancellationToken.None));
            await receiver.PollOnceAsync(Handle, CancellationToken.None);

            Assert.Equal([BackplaneMessageType.ClearAll, BackplaneMessageType.KeyInvalidation], delivered.Select(m => m.Type));
            Assert.Equal("next", delivered[1].Key);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task AnInvalidationStillCommitting_HoldsBackLaterOnes_SoNoneIsSkipped()
    {
        var options = fixture.Options("order");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await using var receiver = new PostgreSqlBackplane(connection, options);
            await using var publisher = new PostgreSqlBackplane(connection, options);
            var delivered = new List<BackplaneMessage>();
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);

            // Another instance writes an invalidation in a transaction that stays open
            await using var slow = new NpgsqlConnection(options.ConnectionString);
            await slow.OpenAsync();
            await using var transaction = await slow.BeginTransactionAsync();
            await using (var insert = new NpgsqlCommand(publisher.PublishSql, slow, transaction))
            {
                insert.Parameters.Add(new NpgsqlParameter<byte[]> { NpgsqlDbType = NpgsqlDbType.Bytea, TypedValue = PostgreSqlBackplane.GetScope("methodcache:") });
                insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = "slow" });
                insert.Parameters.Add(new NpgsqlParameter<short> { TypedValue = BackplaneWire.Version });
                insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = Wire("first", "slow") });
                insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = publisher.Channel });
                await insert.ExecuteNonQueryAsync();
            }

            // A later invalidation waits for it instead of committing first with a higher id
            var later = publisher.PublishInvalidationAsync("later");
            await WaitUntilAsync(() => WaitsForALockAsync(options, TimeSpan.Zero));
            Assert.False(later.IsCompleted);
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);
            Assert.Empty(delivered);

            await transaction.CommitAsync();
            await later.WaitAsync(Eventually);
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);

            Assert.Equal(["first", "later"], delivered.Select(m => m.Key));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task ConcurrentPublishers_AreAllDelivered_InIdOrder()
    {
        var options = fixture.Options("concurrent");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await using var receiver = new PostgreSqlBackplane(connection, options);
            var publishers = Enumerable.Range(0, 4).Select(_ => new PostgreSqlBackplane(connection, options)).ToArray();
            var delivered = new List<BackplaneMessage>();
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);

            var polling = Task.Run(async () =>
            {
                for (var i = 0; i < 20; i++) { await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None); await Task.Delay(5); }
            });
            await Task.WhenAll(publishers.Select((p, n) => Task.Run(async () =>
            {
                for (var i = 0; i < 50; i++) await p.PublishInvalidationAsync($"{n}:{i}");
            })));
            await polling;
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);

            Assert.Equal(200, delivered.Count);
            Assert.Equal(200, delivered.Select(m => m.Key).Distinct().Count());
            // Each publisher's invalidations arrive in the order it wrote them
            foreach (var group in delivered.GroupBy(m => m.Key!.Split(':')[0]))
                Assert.Equal(Enumerable.Range(0, 50).Select(i => $"{group.Key}:{i}"), group.Select(m => m.Key));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task CursorBehindWhatWasRead_ClearsMemory_AndStartsFromTheDatabase()
    {
        var options = fixture.Options("restore");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await using var receiver = new PostgreSqlBackplane(connection, options);
            await using var sender = new PostgreSqlBackplane(connection, options);
            var delivered = new List<BackplaneMessage>();
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);
            await sender.PublishInvalidationAsync("before");
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);

            // A restore from before that invalidation, or a failover that lost it
            await PostgreSqlFixture.ExecuteAsync(options.ConnectionString, $"TRUNCATE {options.QualifiedInvalidationsTableName}, {options.QualifiedCursorTableName}");
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);
            await sender.PublishInvalidationAsync("after");
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);

            Assert.Equal([BackplaneMessageType.KeyInvalidation, BackplaneMessageType.ClearAll, BackplaneMessageType.KeyInvalidation], delivered.Select(m => m.Type));
            Assert.Equal("after", delivered[2].Key);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task OutageLongerThanHalfTheRetention_ClearsMemory_AndSkipsWhatItMissed()
    {
        var options = fixture.Options("outage");
        try
        {
            var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
            await using var connection = await CacheTests.CreateAsync(options);
            await using var receiver = new PostgreSqlBackplane(connection, options, timeProvider: time);
            await using var sender = new PostgreSqlBackplane(connection, options);
            var delivered = new List<BackplaneMessage>();
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);
            await sender.PublishInvalidationAsync("missed");

            time.Advance(options.BackplaneRetention / 2);
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);
            await sender.PublishInvalidationAsync("after");
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);

            Assert.Equal([BackplaneMessageType.ClearAll, BackplaneMessageType.KeyInvalidation], delivered.Select(m => m.Type));
            Assert.Equal("after", delivered[1].Key);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task OutageFromSubscribingToTheFirstPoll_ClearsMemory()
    {
        // PostgreSQL was unreachable from startup: values cached meanwhile may have lost invalidations already deleted
        var options = fixture.Options("outage_first");
        try
        {
            var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
            await using var connection = await CacheTests.CreateAsync(options);
            await using var receiver = new PostgreSqlBackplane(connection, options, timeProvider: time);
            await using var sender = new PostgreSqlBackplane(connection, options);
            await sender.PublishInvalidationAsync("kept");

            time.Advance(options.BackplaneRetention / 2);
            var delivered = new List<BackplaneMessage>();
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);
            await sender.PublishInvalidationAsync("after");
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);

            Assert.Equal([BackplaneMessageType.ClearAll, BackplaneMessageType.KeyInvalidation], delivered.Select(m => m.Type));
            Assert.Equal("after", delivered[1].Key);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task FirstPoll_DeliversAnInvalidationThatWaitedForTheCursor_UntilAfterSubscribing()
    {
        var options = fixture.Options("cursor_wait");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await using var sender = new PostgreSqlBackplane(connection, options);
            await sender.PublishInvalidationAsync("before");

            PostgreSqlBackplane receiver;
            Task late;
            await using (var blocker = new NpgsqlConnection(options.ConnectionString))
            {
                await blocker.OpenAsync();
                await using var transaction = await blocker.BeginTransactionAsync();
                await using (var lockCursor = new NpgsqlCommand($"SELECT 1 FROM {options.QualifiedCursorTableName} FOR UPDATE", blocker, transaction))
                    await lockCursor.ExecuteNonQueryAsync();

                // Its statement starts now, but waits for the cursor row longer than the first poll's margin
                late = sender.PublishInvalidationAsync("late");
                await WaitUntilAsync(() => WaitsForALockAsync(options, TimeSpan.FromMilliseconds(1500)));

                // This instance subscribes, and may cache values, before that invalidation commits
                receiver = new PostgreSqlBackplane(connection, options, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
                await transaction.CommitAsync();
            }

            await late;
            await using (receiver)
            {
                var delivered = new List<BackplaneMessage>();
                await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);
                Assert.Contains("late", delivered.Select(m => m.Key));
            }
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task FirstPoll_ThatWaitsForAConnection_SkipsNoInvalidationWithoutAClear()
    {
        var options = fixture.Options("pool_wait");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await using var sender = new PostgreSqlBackplane(connection, options);
            await using var receiverConnection = new PostgreSqlConnection(
                new NpgsqlConnectionStringBuilder(options.ConnectionString) { MaxPoolSize = 1 }.ConnectionString);
            // The real clock: the wait for a connection is what must not move where reading starts
            await using var receiver = new PostgreSqlBackplane(receiverConnection, options);
            var delivered = new List<BackplaneMessage>();

            Task poll;
            await using (var held = await receiverConnection.DataSource.OpenConnectionAsync())
            {
                // The first poll starts, then waits for the pool's only connection
                poll = receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);
                await sender.PublishInvalidationAsync("after subscribing");
                await WaitUntilAsync(() => PostgreSqlFixture.ScalarAsync<bool>(options.ConnectionString,
                    $"SELECT coalesce(bool_and(now() - created_at > interval '1500 milliseconds'), false) FROM {options.QualifiedInvalidationsTableName}"));
            }

            await poll;
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);

            Assert.Contains(delivered, m => m.Key == "after subscribing" || m.Type == BackplaneMessageType.ClearAll);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task CursorRecreated_ByARestore_ClearsMemory_EvenWhenAnotherInvalidationCameFirst()
    {
        var options = fixture.Options("restore");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await using var receiver = new PostgreSqlBackplane(connection, options);
            await using var sender = new PostgreSqlBackplane(connection, options);
            var delivered = new List<BackplaneMessage>();
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);
            delivered.Clear();
            await sender.PublishInvalidationAsync("lost");

            // A restore from before that invalidation; the next one gets a higher id than this instance read up to
            await PostgreSqlFixture.ExecuteAsync(options.ConnectionString, $"TRUNCATE {options.QualifiedInvalidationsTableName}, {options.QualifiedCursorTableName}");
            await sender.PublishInvalidationAsync("unrelated");
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);

            Assert.Contains(delivered, m => m.Type == BackplaneMessageType.ClearAll);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task CursorTableRestoredFromADump_ClearsMemory_ThoughItKeepsTheGeneration()
    {
        var options = fixture.Options("dump");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await using var receiver = new PostgreSqlBackplane(connection, options);
            await using var sender = new PostgreSqlBackplane(connection, options);
            var delivered = new List<BackplaneMessage>();
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);
            await sender.PublishInvalidationAsync("dumped");
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);
            delivered.Clear();

            // pg_dump copies the unlogged cursor row too, generation included
            var schema = PostgreSqlOptions.Quote(options.SchemaName);
            await PostgreSqlFixture.ExecuteAsync(options.ConnectionString, $"CREATE TABLE {schema}.dump AS SELECT * FROM {options.QualifiedCursorTableName}");
            await sender.PublishInvalidationAsync("lost");

            // Restored into tables created anew; the next invalidation gets a higher id than this instance read up to
            await PostgreSqlFixture.ExecuteAsync(options.ConnectionString, $"""
                DELETE FROM {options.QualifiedInvalidationsTableName} WHERE id > (SELECT last_id FROM {schema}.dump);
                ALTER TABLE {options.QualifiedCursorTableName} RENAME TO replaced;
                CREATE UNLOGGED TABLE {options.QualifiedCursorTableName} (LIKE {schema}.replaced INCLUDING ALL);
                INSERT INTO {options.QualifiedCursorTableName} SELECT * FROM {schema}.dump;
                """);
            await sender.PublishInvalidationAsync("unrelated");
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);

            Assert.Contains(delivered, m => m.Type == BackplaneMessageType.ClearAll);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task CursorEmptiedByACrash_ClearsMemory_WhenTheLastInvalidationsWereLost()
    {
        // Invalidations commit asynchronously, so a crash can lose the last ones; recovery also empties the unlogged cursor
        var options = fixture.Options("crash");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await using var receiver = new PostgreSqlBackplane(connection, options);
            await using var sender = new PostgreSqlBackplane(connection, options);
            var delivered = new List<BackplaneMessage>();
            await sender.PublishInvalidationAsync("kept");
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);
            delivered.Clear();
            await sender.PublishInvalidationAsync("lost");

            Assert.Equal(1, await PostgreSqlFixture.ScalarAsync<long>(options.ConnectionString,
                $"WITH lost AS (DELETE FROM {options.QualifiedInvalidationsTableName} WHERE id = (SELECT max(id) FROM {options.QualifiedInvalidationsTableName}) RETURNING id) SELECT count(*) FROM lost"));
            await PostgreSqlFixture.ExecuteAsync(options.ConnectionString, $"TRUNCATE {options.QualifiedCursorTableName}");
            Assert.Equal("u", await PostgreSqlFixture.ScalarAsync<string>(options.ConnectionString,
                $"SELECT relpersistence::text FROM pg_class WHERE oid = '{options.QualifiedCursorTableName}'::regclass"));
            await sender.PublishInvalidationAsync("after the crash");
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);
            await sender.PublishInvalidationAsync("later");
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);

            // The clear covers everything up to the cursor it read; what commits after that arrives as usual
            Assert.Equal([BackplaneMessageType.ClearAll, BackplaneMessageType.KeyInvalidation], delivered.Select(m => m.Type));
            Assert.Equal("later", delivered[1].Key);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task NewCursor_ContinuesAfterTheHighestId_EvenIfTheClockWentBack()
    {
        var options = fixture.Options("clock");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await using var receiver = new PostgreSqlBackplane(connection, options);
            await using var sender = new PostgreSqlBackplane(connection, options);
            var delivered = new List<BackplaneMessage>();
            await sender.PublishInvalidationAsync("before");
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);

            // Ids an hour ahead of the server's clock, as after a failover to a server whose clock is behind; then a
            // crash empties the cursor table
            var ahead = await PostgreSqlFixture.ScalarAsync<long>(options.ConnectionString,
                $"UPDATE {options.QualifiedInvalidationsTableName} SET id = id + 3600000000 RETURNING id");
            await PostgreSqlFixture.ExecuteAsync(options.ConnectionString, $"TRUNCATE {options.QualifiedCursorTableName}");
            await sender.PublishInvalidationAsync("after");

            Assert.Equal(ahead + 1, await PostgreSqlFixture.ScalarAsync<long>(options.ConnectionString,
                $"SELECT max(id) FROM {options.QualifiedInvalidationsTableName}"));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task FirstPoll_DeliversRecentInvalidations_ButNotOldOnes()
    {
        var options = fixture.Options("start");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await using var sender = new PostgreSqlBackplane(connection, options);
            await sender.PublishInvalidationAsync("old");
            await PostgreSqlFixture.ExecuteAsync(options.ConnectionString, $"UPDATE {options.QualifiedInvalidationsTableName} SET created_at = now() - interval '10 minutes'");
            await sender.PublishInvalidationAsync("recent");

            // Started just now: it may have cached values in the second before it subscribed
            await using var receiver = new PostgreSqlBackplane(connection, options);
            var delivered = new List<BackplaneMessage>();
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);

            Assert.Equal("recent", Assert.Single(delivered).Key);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task ABurstOfInvalidations_IsReadInOnePoll()
    {
        var options = fixture.Options("burst");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await using var receiver = new PostgreSqlBackplane(connection, options);
            await using var sender = new PostgreSqlBackplane(connection, options);
            var delivered = new List<BackplaneMessage>();
            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);
            await InsertAsync(options, 3000, "unrelated");
            await sender.PublishInvalidationAsync("key");

            await receiver.PollOnceAsync(Collect(delivered), CancellationToken.None);

            Assert.Equal(3001, delivered.Count);
            Assert.Equal("key", delivered[^1].Key);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task Cleanup_RemovesExpiredInvalidations_OfItsOwnPrefixOnly()
    {
        var options = fixture.Options("cleanup");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await using var receiver = new PostgreSqlBackplane(connection, options);
            await InsertAsync(options, 12000, "old", age: TimeSpan.FromHours(2));
            await InsertAsync(options, 10, "other", age: TimeSpan.FromHours(2), prefix: "other:");
            await InsertAsync(options, 10, "recent");

            // Already the first poll cleans up, then once a minute
            await receiver.PollOnceAsync(_ => Task.CompletedTask, CancellationToken.None);

            Assert.Equal(20L, await PostgreSqlFixture.ScalarAsync<long>(options.ConnectionString, $"SELECT count(*) FROM {options.QualifiedInvalidationsTableName}"));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task Cleanup_KeepsInvalidations_AsLongAsTheLongestRetentionOfItsPrefix()
    {
        // During a rolling deployment that shortens the retention, an instance that still uses the longer one may be
        // out for longer than the shorter retention: it must still find what it missed, since it won't clear its memory
        var options = fixture.Options("retention");
        var shorter = new PostgreSqlOptions { ConnectionString = options.ConnectionString, SchemaName = options.SchemaName, BackplaneRetention = TimeSpan.FromMinutes(1) };
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await using var longer = new PostgreSqlBackplane(connection, options);
            await longer.PollOnceAsync(_ => Task.CompletedTask, CancellationToken.None);
            await InsertAsync(options, 10, "within the longer retention", age: TimeSpan.FromMinutes(10));
            await InsertAsync(options, 10, "past both", age: TimeSpan.FromHours(2));

            await using var cleaner = new PostgreSqlBackplane(connection, shorter);
            await cleaner.PollOnceAsync(_ => Task.CompletedTask, CancellationToken.None);

            Assert.Equal(10L, await PostgreSqlFixture.ScalarAsync<long>(options.ConnectionString, $"SELECT count(*) FROM {options.QualifiedInvalidationsTableName}"));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task Notification_DeliversAtOnce_WithoutWaitingForThePoll()
    {
        var options = fixture.Options("notify");
        options.BackplanePollingInterval = TimeSpan.FromMinutes(10);
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await using var receiver = new PostgreSqlBackplane(connection, options);
            await using var sender = new PostgreSqlBackplane(connection, options);
            var delivered = new TaskCompletionSource<BackplaneMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            await receiver.SubscribeAsync(message =>
            {
                if (message.Key == "key") delivered.TrySetResult(message);
                return Task.CompletedTask;
            });
            await WaitUntilAsync(() => receiver.Listening);

            // The very first invalidation after subscribing: the poll it triggers both finds where to start and delivers it
            await sender.PublishInvalidationAsync("key");

            Assert.Equal("key", (await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5))).Key);
            Assert.Equal(BackplaneState.Connected, receiver.ConnectionState);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task LostListenConnection_Reconnects()
    {
        var options = fixture.Options("relisten");
        options.BackplanePollingInterval = TimeSpan.FromMinutes(10);
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await using var receiver = new PostgreSqlBackplane(connection, options);
            await using var sender = new PostgreSqlBackplane(connection, options);
            var delivered = new List<string>();
            await receiver.SubscribeAsync(message => { lock (delivered) delivered.Add(message.Key!); return Task.CompletedTask; });
            await WaitUntilAsync(() => receiver.Listening);
            await WaitUntilAsync(() => receiver.ConnectionState == BackplaneState.Connected);

            await PostgreSqlFixture.ExecuteAsync(options.ConnectionString,
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE query = $1", "LISTEN " + PostgreSqlOptions.Quote(receiver.Channel));
            await WaitUntilAsync(() => !receiver.Listening);
            // Its notification is lost, and the next poll is ten minutes away: reconnecting reads it
            await sender.PublishInvalidationAsync("while-down");
            await WaitUntilAsync(() => { lock (delivered) return delivered.Contains("while-down"); });
            await WaitUntilAsync(() => receiver.Listening);
            await sender.PublishInvalidationAsync("after");

            await WaitUntilAsync(() => { lock (delivered) return delivered.Contains("after"); });
            lock (delivered) Assert.Equal(["while-down", "after"], delivered);
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task InvalidationsThatRingNoDoorbell_ArriveByPolling_WithOneWarning()
    {
        // As behind a pooler in transaction mode: LISTEN succeeds, but no notification ever arrives
        var options = fixture.Options("silent");
        options.BackplanePollingInterval = TimeSpan.FromMilliseconds(50);
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            var logger = new WarningLogger();
            await using var receiver = new PostgreSqlBackplane(connection, options, logger);
            var delivered = new List<string>();
            await receiver.SubscribeAsync(message => { lock (delivered) delivered.Add(message.Key!); return Task.CompletedTask; });
            await WaitUntilAsync(() => receiver.Listening);

            foreach (var key in new[] { "first", "second", "third", "fourth", "fifth" })
            {
                // Written like another instance's invalidation, without its NOTIFY
                await InsertAsync(options, 1, key);
                await WaitUntilAsync(() => { lock (delivered) return delivered.Contains(key); });
            }

            lock (logger.Warnings) Assert.Single(logger.Warnings, warning => warning.Contains("receives no PostgreSQL notifications"));
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    [SkippableFact]
    public async Task Unsubscribe_StopsPollingAndListening()
    {
        var options = fixture.Options("stop");
        try
        {
            await using var connection = await CacheTests.CreateAsync(options);
            await using var receiver = new PostgreSqlBackplane(connection, options);
            await receiver.SubscribeAsync(_ => Task.CompletedTask);
            await WaitUntilAsync(() => receiver.Listening);

            await receiver.UnsubscribeAsync().WaitAsync(Eventually);

            Assert.False(receiver.Listening);
            Assert.Equal(BackplaneState.Disconnected, receiver.ConnectionState);
            var deadline = DateTime.UtcNow + Eventually;
            while (await PostgreSqlFixture.ScalarAsync<long>(options.ConnectionString,
                       "SELECT count(*) FROM pg_stat_activity WHERE query = $1", "LISTEN " + PostgreSqlOptions.Quote(receiver.Channel)) != 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "the LISTEN connection stayed open");
                await Task.Delay(50);
            }
        }
        finally { await PostgreSqlFixture.DropSchemaAsync(options); }
    }

    private static Func<BackplaneMessage, Task> Collect(List<BackplaneMessage> messages) => message =>
    {
        lock (messages) messages.Add(message);
        return Task.CompletedTask;
    };

    private static string Wire(string key, string sender) => BackplaneWire.Encode(new BackplaneMessage
    { Type = BackplaneMessageType.KeyInvalidation, Key = key, InstanceId = sender });

    /// <summary>Rows written the way the backplane writes them, from an instance named "other".</summary>
    /// <summary>Whether a statement on this test's tables has waited for a lock for longer than <paramref name="waited"/>.</summary>
    private static Task<bool> WaitsForALockAsync(PostgreSqlOptions options, TimeSpan waited) => PostgreSqlFixture.ScalarAsync<bool>(options.ConnectionString,
        "SELECT coalesce(bool_or(now() - query_start >= $2), false) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND strpos(query, $1) > 0",
        options.SchemaName, waited);

    private static Task InsertAsync(PostgreSqlOptions options, int count, string key, TimeSpan age = default, string prefix = "methodcache:")
        => PostgreSqlFixture.ExecuteAsync(options.ConnectionString, $"""
            WITH next AS (
                INSERT INTO {options.QualifiedCursorTableName} AS c (scope, last_id) VALUES ($1, $3)
                ON CONFLICT (scope) DO UPDATE SET last_id = c.last_id + $3
                RETURNING last_id)
            INSERT INTO {options.QualifiedInvalidationsTableName} (scope, id, instance_id, message_type, target, created_at)
            SELECT $1, next.last_id - $3 + i, 'other', 1, $2, now() - $4 FROM next, generate_series(1, $3) AS i
            """, PostgreSqlBackplane.GetScope(prefix), Wire(key, "other"), (long)count, age);

    private sealed class WarningLogger : Microsoft.Extensions.Logging.ILogger<PostgreSqlBackplane>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning) lock (Warnings) Warnings.Add(formatter(state, exception));
        }
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Eventually;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(20);
        }
    }

    internal static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Eventually;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(20);
        }
    }
}
