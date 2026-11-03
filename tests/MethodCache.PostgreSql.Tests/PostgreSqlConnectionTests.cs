using FluentAssertions;
using Npgsql;

namespace MethodCache.PostgreSql.Tests;

public class PostgreSqlConnectionTests
{
    [Fact]
    public void Commands_ArePreparedAutomatically_SkipThePoolReset_AndAreNamed()
    {
        using var connection = new PostgreSqlConnection("Host=localhost;Database=cache");
        var commands = new NpgsqlConnectionStringBuilder(connection.CommandConnectionString);

        commands.MaxAutoPrepare.Should().Be(PostgreSqlConnection.DefaultMaxAutoPrepare);
        commands.NoResetOnClose.Should().BeTrue();
        commands.ApplicationName.Should().Be("MethodCache");
        commands.Pooling.Should().BeTrue();
    }

    [Fact]
    public void ExplicitSettings_AreKept()
    {
        // Max Auto Prepare=0 is how a pooler without prepared statement support is configured
        using var connection = new PostgreSqlConnection("Host=localhost;Database=cache;Max Auto Prepare=0;No Reset On Close=false;Application Name=shop;Keepalive=5");
        var commands = new NpgsqlConnectionStringBuilder(connection.CommandConnectionString);
        var listen = new NpgsqlConnectionStringBuilder(connection.ListenConnectionString);

        commands.MaxAutoPrepare.Should().Be(0);
        commands.NoResetOnClose.Should().BeFalse();
        commands.ApplicationName.Should().Be("shop");
        listen.KeepAlive.Should().Be(5);
    }

    [Theory]
    [InlineData("Host=localhost;Database=cache")]
    [InlineData("Host=localhost;Database=cache;Enlist=true")]
    public void Commands_NeverJoinTheApplicationsTransactionScope(string connectionString)
    {
        using var connection = new PostgreSqlConnection(connectionString);

        new NpgsqlConnectionStringBuilder(connection.CommandConnectionString).Enlist.Should().BeFalse();
        new NpgsqlConnectionStringBuilder(connection.ListenConnectionString).Enlist.Should().BeFalse();
    }

    [Fact]
    public void Listen_UsesItsOwnConnection_WithKeepAlive()
    {
        using var connection = new PostgreSqlConnection("Host=localhost;Database=cache;Maximum Pool Size=10");
        var listen = new NpgsqlConnectionStringBuilder(connection.ListenConnectionString);

        listen.Pooling.Should().BeFalse("LISTEN holds its connection for as long as the instance runs");
        listen.MaxAutoPrepare.Should().Be(0);
        listen.KeepAlive.Should().Be((int)PostgreSqlConnection.ListenKeepAlive.TotalSeconds);
        listen.Host.Should().Be("localhost");
        listen.Database.Should().Be("cache");
    }
}
