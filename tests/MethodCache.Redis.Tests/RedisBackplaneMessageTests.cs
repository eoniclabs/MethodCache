using FluentAssertions;
using MethodCache.Extensibility;
using StackExchange.Redis;
using Xunit;

namespace MethodCache.Redis.Tests;

public class RedisBackplaneMessageTests
{
    [Theory]
    [InlineData("0\nsender\n4:awBlAHkA")]
    [InlineData("1\nsender\n4:awBlAHkA")]
    public void UnsupportedMessageType_WithVersionLookingKey_IsRejected(string wire)
        => Assert.False(RedisBackplane.TryParse(wire, out _));

    [Fact]
    public void EncodedMessages_UseDistinctMessageTypeNumbers()
    {
        Assert.Equal(1, Convert.FromBase64String(RedisBackplane.Format(BackplaneMessageType.KeyInvalidation, "sender", "key")[4..])[1]);
        Assert.Equal(2, Convert.FromBase64String(RedisBackplane.Format(BackplaneMessageType.TagInvalidation, "sender", "tag")[4..])[1]);
    }

    [Theory]
    [InlineData("user:1")]
    [InlineData("")]
    [InlineData("multi\nline\nvalue")]
    public void KeyInvalidation_RoundTrips(string key)
    {
        var payload = RedisBackplane.Format(BackplaneMessageType.KeyInvalidation, "instance", key);

        RedisBackplane.TryParse(payload, out var message).Should().BeTrue();

        message.Type.Should().Be(BackplaneMessageType.KeyInvalidation);
        message.Key.Should().Be(key);
        message.InstanceId.Should().Be("instance");
    }

    [Fact]
    public void TagInvalidation_Utf8Transport_PreservesAllUtf16Units()
    {
        foreach (var tag in new[] { "tag:\ud800", "tag:\ud801", "tag:\udfff", "tag:\0", "tag:\ufffd" })
        {
            var payload = RedisBackplane.Format(BackplaneMessageType.TagInvalidation, "instance", tag);
            RedisValue transported = System.Text.Encoding.UTF8.GetBytes(payload);
            Assert.True(RedisBackplane.TryParse(transported, out var message));
            Assert.Equal(tag, message.Tag);
        }
    }

    [Fact]
    public void TagInvalidation_RoundTrips()
    {
        var payload = RedisBackplane.Format(BackplaneMessageType.TagInvalidation, "instance", "tag:users");

        RedisBackplane.TryParse(payload, out var message).Should().BeTrue();

        message.Type.Should().Be(BackplaneMessageType.TagInvalidation);
        message.Tag.Should().Be("tag:users");
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("0\ninstance")]
    [InlineData("x\ninstance\nkey")]
    [InlineData("9\ninstance\nkey")]
    public void MalformedMessages_AreIgnored(string payload)
    {
        RedisBackplane.TryParse(payload, out _).Should().BeFalse();
    }

    [Fact]
    public void NullMessage_IsIgnored()
    {
        RedisBackplane.TryParse(RedisValue.Null, out _).Should().BeFalse();
    }
}
