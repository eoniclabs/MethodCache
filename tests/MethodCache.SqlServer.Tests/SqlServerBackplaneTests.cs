using FluentAssertions;
using MethodCache.Core.Storage.Abstractions;

namespace MethodCache.SqlServer.Tests;

public class SqlServerBackplaneTests
{
    private static readonly DateTime CreatedAt = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Unspecified);

    [Fact]
    public void CreateMessage_KeyInvalidation()
    {
        var message = SqlServerBackplane.CreateMessage(BackplaneMessageType.KeyInvalidation, "user:1", "other", CreatedAt);

        message.Should().NotBeNull();
        message!.Key.Should().Be("user:1");
        message.Tag.Should().BeNull();
        message.InstanceId.Should().Be("other");
        message.Timestamp.Should().Be(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void CreateMessage_TagInvalidation()
    {
        var message = SqlServerBackplane.CreateMessage(BackplaneMessageType.TagInvalidation, "users", "other", CreatedAt);

        message!.Tag.Should().Be("users");
        message.Key.Should().BeNull();
    }

    [Fact]
    public void CreateMessage_UnknownType_IsIgnored()
    {
        SqlServerBackplane.CreateMessage((BackplaneMessageType)42, "x", "other", CreatedAt).Should().BeNull();
    }

    [Fact]
    public async Task Unsubscribe_WithoutSubscription_DoesNothing()
    {
        await using var backplane = new SqlServerBackplane(new SqlServerOptions { ConnectionString = "Server=." });

        await backplane.UnsubscribeAsync();
    }
}
