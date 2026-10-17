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
        var valid = RedisBackplane.Format(BackplaneMessageType.KeyInvalidation, "sender", "key");
        var bytes = Convert.FromBase64String(valid[4..]);
        bytes[1] = 127; // Valid MC1 envelope, unknown operation.
        receiver!(RedisChannel.Literal("scope:invalidations"), "MC1:" + Convert.ToBase64String(bytes));
        bytes = Convert.FromBase64String(valid[4..]);
        var extended = new byte[bytes.Length + 5];
        bytes.CopyTo(extended, 0);
        extended[bytes.Length] = 0xff; // Unknown mandatory TLV with a zero-length payload.
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(extended.AsSpan(4), extended.Length - 8);
        receiver!(RedisChannel.Literal("scope:invalidations"), "MC1:" + Convert.ToBase64String(extended));
        Assert.Equal(4, messages.Count);
        Assert.All(messages, message => Assert.Equal(BackplaneMessageType.ClearAll, message.Type));
    }
}
