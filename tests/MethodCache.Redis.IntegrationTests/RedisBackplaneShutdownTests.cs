using MethodCache.Extensibility;
using StackExchange.Redis;
using Xunit;

namespace MethodCache.Redis.IntegrationTests;

[Collection(RedisCollection.Name)]
public class RedisBackplaneShutdownTests(RedisFixture redis)
{
    [SkippableFact]
    public async Task RepeatedSubscriptionReconnect_DeliversOnceAndPreservesBorrowedConnection()
    {
        var name = "methodcache-reconnect-test-" + Guid.NewGuid().ToString("N");
        var options = ConfigurationOptions.Parse(redis.RequireConfiguration());
        options.ClientName = name;
        options.AllowAdmin = true;
        using var connection = await ConnectionMultiplexer.ConnectAsync(options);
        var channel = RedisChannel.Literal(name);
        var deliveries = 0;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var backplane = new RedisBackplane(() => Task.FromResult<IConnectionMultiplexer>(connection), name);
        await backplane.SubscribeAsync(message =>
        {
            if (message.Key == "value") Interlocked.Increment(ref deliveries);
            if (message.Key == "barrier") barrier.TrySetResult();
            return Task.CompletedTask;
        });
        var server = connection.GetServer(connection.GetEndPoints()[0]);
        for (var cycle = 0; cycle < 2; cycle++)
        {
            var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnRestored(object? _, ConnectionFailedEventArgs args)
            { if (args.ConnectionType == ConnectionType.Subscription) restored.TrySetResult(); }
            connection.ConnectionRestored += OnRestored;
            try
            {
                var client = Assert.Single(await server.ClientListAsync(), info => info.Name?.StartsWith(name, StringComparison.Ordinal) == true && info.ClientType == ClientType.PubSub);
                // Kill only this test's unique pub/sub connection; every other client remains untouched.
                Assert.Equal(1, await server.ClientKillAsync(id: client.Id));
                await restored.Task.WaitAsync(TimeSpan.FromSeconds(30));
                await backplane.RecoveryCompletion.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(BackplaneState.Connected, backplane.ConnectionState);
            }
            finally { connection.ConnectionRestored -= OnRestored; }
        }
        var subscriber = connection.GetSubscriber();
        await subscriber.PublishAsync(channel, RedisBackplane.Format(BackplaneMessageType.KeyInvalidation, "remote", "value"));
        await subscriber.PublishAsync(channel, RedisBackplane.Format(BackplaneMessageType.KeyInvalidation, "remote", "barrier"));
        await barrier.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, Volatile.Read(ref deliveries));
        await backplane.DisposeAsync();
        Assert.True(connection.IsConnected);
        await connection.GetDatabase().PingAsync();
    }

    [SkippableFact]
    public async Task DisposeAsync_BorrowedConnection_PreservesOtherSubscribersAndTheConnection()
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(redis.RequireConfiguration());
        var channel = RedisChannel.Literal($"shutdown:{Guid.NewGuid():N}");
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriber = connection.GetSubscriber();
        Action<RedisChannel, RedisValue> other = (_, _) => received.TrySetResult();
        await subscriber.SubscribeAsync(channel, other);
        var backplane = new RedisBackplane(() => Task.FromResult<IConnectionMultiplexer>(connection), channel.ToString());
        await backplane.SubscribeAsync(_ => Task.CompletedTask);
        try
        {
            await backplane.DisposeAsync();
            Assert.True(connection.IsConnected);
            var delivered = await subscriber.PublishAsync(channel, RedisBackplane.Format(BackplaneMessageType.KeyInvalidation, "remote", "key"));
            Assert.Equal(1, delivered);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await connection.GetDatabase().PingAsync();
        }
        finally
        {
            await backplane.DisposeAsync();
            await subscriber.UnsubscribeAsync(channel, other);
        }
    }
}
