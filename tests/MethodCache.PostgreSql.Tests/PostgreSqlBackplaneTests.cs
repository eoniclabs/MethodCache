using System.Text;
using FluentAssertions;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;

namespace MethodCache.PostgreSql.Tests;

public class PostgreSqlBackplaneTests
{
    [Theory]
    [InlineData("a", "A")]
    [InlineData("\u00e9", "e\u0301")]
    [InlineData("", "\0")]
    public void Scope_ExactUtf16Prefixes_StayDistinct(string first, string second)
    {
        PostgreSqlBackplane.GetScope(first).Should().HaveCount(32).And.Equal(PostgreSqlBackplane.GetScope(first));
        PostgreSqlBackplane.GetScope(first).Should().NotEqual(PostgreSqlBackplane.GetScope(second));
    }

    [Fact]
    public void Scope_UnpairedSurrogates_AreNotReplacedByAnEncoder()
    {
        // Built in code: attribute arguments can't carry lone surrogates
        var first = new string((char)0xd800, 1);

        PostgreSqlBackplane.GetScope(first).Should().NotEqual(PostgreSqlBackplane.GetScope(new string((char)0xd801, 1)));
        PostgreSqlBackplane.GetScope(first).Should().NotEqual(PostgreSqlBackplane.GetScope("\ufffd"));
    }

    [Fact]
    public void Channel_IsAShortLowercaseIdentifier()
    {
        var channel = PostgreSqlBackplane.GetChannel(new PostgreSqlOptions(), PostgreSqlBackplane.GetScope(new string('x', 10_000)));

        channel.Should().MatchRegex("^methodcache_[0-9a-f]{32}$");
        Encoding.UTF8.GetByteCount(channel).Should().BeLessThanOrEqualTo(63);
    }

    [Fact]
    public void Channel_DiffersByPrefixAndByTable()
    {
        var options = new PostgreSqlOptions();
        var scope = PostgreSqlBackplane.GetScope("methodcache:");

        var channel = PostgreSqlBackplane.GetChannel(options, scope);

        PostgreSqlBackplane.GetChannel(new PostgreSqlOptions(), scope).Should().Be(channel);
        PostgreSqlBackplane.GetChannel(options, PostgreSqlBackplane.GetScope("other:")).Should().NotBe(channel);
        PostgreSqlBackplane.GetChannel(new PostgreSqlOptions { SchemaName = "cache" }, scope).Should().NotBe(channel);
        PostgreSqlBackplane.GetChannel(new PostgreSqlOptions { InvalidationsTableName = "other" }, scope).Should().NotBe(channel);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(42)]
    public void CreateWireMessage_RejectsUnsupportedVersions(short type)
    {
        var frame = BackplaneWire.Encode(new BackplaneMessage { Type = BackplaneMessageType.KeyInvalidation, Key = "key", InstanceId = "other" });

        PostgreSqlBackplane.CreateWireMessage(type, frame, "other").Should().BeNull();
    }

    [Theory]
    [InlineData((int)BackplaneMessageType.KeyInvalidation)]
    [InlineData((int)BackplaneMessageType.TagInvalidation)]
    [InlineData((int)BackplaneMessageType.MethodInvalidation)]
    [InlineData((int)BackplaneMessageType.ClearAll)]
    public void CreateWireMessage_DecodesEveryMessageType(int logicalValue)
    {
        var logical = (BackplaneMessageType)logicalValue;
        var frame = BackplaneWire.Encode(new BackplaneMessage
        {
            Type = logical,
            InstanceId = "other",
            Key = logical is BackplaneMessageType.KeyInvalidation or BackplaneMessageType.MethodInvalidation ? "key" : null,
            Tag = logical == BackplaneMessageType.TagInvalidation ? "key" : null
        });

        var message = PostgreSqlBackplane.CreateWireMessage(BackplaneWire.Version, frame, "other");

        message.Should().NotBeNull();
        message!.Type.Should().Be(logical);
        PostgreSqlBackplane.CreateWireMessage(BackplaneWire.Version, "invalid", "other").Should().BeNull();
    }

    [Fact]
    public void CreateWireMessage_SenderMustMatchTheRow()
    {
        var frame = BackplaneWire.Encode(new BackplaneMessage { Type = BackplaneMessageType.KeyInvalidation, Key = "key", InstanceId = "other" });

        PostgreSqlBackplane.CreateWireMessage(BackplaneWire.Version, frame, "someone-else").Should().BeNull();
    }

    [Fact]
    public async Task Unsubscribe_WithoutSubscription_DoesNothing()
    {
        await using var connection = new PostgreSqlConnection("Host=localhost");
        await using var backplane = new PostgreSqlBackplane(connection, new PostgreSqlOptions { ConnectionString = "Host=localhost" });

        await backplane.UnsubscribeAsync();
        backplane.ConnectionState.Should().Be(BackplaneState.Disconnected);
    }

    [Fact]
    public async Task Subscribe_Twice_Throws()
    {
        await using var connection = new PostgreSqlConnection("Host=127.0.0.1;Port=1;Timeout=1");
        await using var backplane = new PostgreSqlBackplane(connection, new PostgreSqlOptions { ConnectionString = "Host=127.0.0.1;Port=1" });

        await backplane.SubscribeAsync(_ => Task.CompletedTask);
        var again = () => backplane.SubscribeAsync(_ => Task.CompletedTask);

        await again.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void InstanceId_IsUniquePerBackplane()
    {
        using var connection = new PostgreSqlConnection("Host=localhost");
        var options = new PostgreSqlOptions { ConnectionString = "Host=localhost" };

        new PostgreSqlBackplane(connection, options).InstanceId.Should().NotBe(new PostgreSqlBackplane(connection, options).InstanceId);
    }
}
