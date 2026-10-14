using FluentAssertions;
using MethodCache.Extensibility;

namespace MethodCache.SqlServer.Tests;

public class SqlServerBackplaneTests
{
    private static readonly DateTime CreatedAt = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Unspecified);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(42)]
    public void CreateWireMessage_RejectsUnsupportedTypes(byte type)
        => Assert.Null(SqlServerBackplane.CreateWireMessage(type, "4:AAAA==", "other", CreatedAt));

    [Theory]
    [InlineData(4, (int)BackplaneMessageType.KeyInvalidation)]
    [InlineData(5, (int)BackplaneMessageType.TagInvalidation)]
    [InlineData(6, (int)BackplaneMessageType.MethodInvalidation)]
    public void CreateWireMessage_DecodesOnlyVersionedMessageTypes(byte type, int logicalValue)
    {
        var logical = (BackplaneMessageType)logicalValue;
        var message = SqlServerBackplane.CreateWireMessage(type, "4:awBlAHkA", "other", CreatedAt);
        Assert.NotNull(message);
        Assert.Equal(logical, message.Type);
        Assert.Equal("key", logical == BackplaneMessageType.TagInvalidation ? message.Tag : message.Key);
        Assert.Null(SqlServerBackplane.CreateWireMessage(type, "invalid", "other", CreatedAt));
    }

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

    [Fact]
    public void RemoveTaken_KeepsOnlyIdsNoRowTakes_AndTheirDeliveredIds()
    {
        var gaps = new List<SqlServerBackplane.GapRange>
        {
            new(10, 20, CreatedAt) { Delivered = { 12, 19 } },
            new(30, 31, CreatedAt),
            new(40, 45, CreatedAt.AddSeconds(1)),
        };

        var holes = SqlServerBackplane.RemoveTaken(gaps, new List<(long, long)> { (5, 11), (13, 18), (20, 35), (41, 41) });

        holes.Select(gap => (gap.From, gap.To)).Should().Equal((12, 12), (19, 19), (40, 40), (42, 45));
        holes[0].Delivered.Should().Equal(12);
        holes[1].Delivered.Should().Equal(19);
        holes[2].SeenAt.Should().Be(CreatedAt.AddSeconds(1));
    }

    [Fact]
    public void RemoveTaken_NothingTaken_KeepsTheGaps()
    {
        var gaps = new List<SqlServerBackplane.GapRange> { new(1, 3, CreatedAt) };

        SqlServerBackplane.RemoveTaken(gaps, new List<(long, long)>()).Select(gap => (gap.From, gap.To)).Should().Equal((1, 3));
    }

    [Fact]
    public void RemoveTaken_ManySeparateHoles_AreWidenedToTheLimit()
    {
        var gaps = new List<SqlServerBackplane.GapRange> { new(1, 1000, CreatedAt) { Delivered = { 999 } } };
        var taken = Enumerable.Range(0, 499).Select(i => ((long)(2 * i + 2), (long)(2 * i + 2))).ToList();

        var holes = SqlServerBackplane.RemoveTaken(gaps, taken);

        holes.Should().HaveCount(100);
        holes.Take(99).Select(gap => (gap.From, gap.To)).Should().Equal(Enumerable.Range(0, 99).Select(i => ((long)(2 * i + 1), (long)(2 * i + 1))));
        (holes[^1].From, holes[^1].To).Should().Be((199L, 1000L));
        holes[^1].Delivered.Should().Equal(999);
    }
}
