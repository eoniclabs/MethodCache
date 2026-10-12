using MethodCache.Extensibility;
using StackExchange.Redis;
using Xunit;

namespace MethodCache.Redis.IntegrationTests;

[Collection(RedisCollection.Name)]
public class RedisBackplaneShutdownTests(RedisFixture redis)
{
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
