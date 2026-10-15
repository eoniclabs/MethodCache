using MethodCache.Extensibility;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace MethodCache.Redis.Tests;

public class MalformedFrameRecoveryTests
{
    [Fact]
    public async Task MalformedFrames_RetireLocalEntriesInsteadOfDroppingTheInvalidation()
    {
        Action<RedisChannel, RedisValue>? receiver = null;
        var subscriber = Substitute.For<ISubscriber>();
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>())
            .Returns(call => { receiver = call.ArgAt<Action<RedisChannel, RedisValue>>(1); return Task.CompletedTask; });
        var connection = Substitute.For<IConnectionMultiplexer>();
        connection.GetSubscriber(Arg.Any<object>()).Returns(subscriber);
        using var backplane = new RedisBackplane(() => Task.FromResult(connection), "scope:invalidations");
        var messages = new List<BackplaneMessage>();
        await backplane.SubscribeAsync(message => { messages.Add(message); return Task.CompletedTask; });
        receiver!(RedisChannel.Literal("scope:invalidations"), "malformed");
        receiver!(RedisChannel.Literal("scope:invalidations"), "unknown-version");
        Assert.Equal(2, messages.Count);
        Assert.All(messages, message => Assert.Equal(BackplaneMessageType.ClearAll, message.Type));
    }
}
