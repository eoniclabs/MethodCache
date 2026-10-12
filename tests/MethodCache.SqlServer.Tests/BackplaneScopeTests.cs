using Xunit;

namespace MethodCache.SqlServer.Tests;

public class BackplaneScopeTests
{
    [Theory]
    [InlineData("a", "A")]
    [InlineData("é", "é")]
    [InlineData("", "\0")]
    public void Scope_ExactUtf16Prefixes_StayDistinct(string first, string second)
    {
        Assert.Equal(32, SqlServerBackplane.GetScope(first).Length);
        Assert.Equal(SqlServerBackplane.GetScope(first), SqlServerBackplane.GetScope(first));
        Assert.NotEqual(SqlServerBackplane.GetScope(first), SqlServerBackplane.GetScope(second));
    }

    [Fact]
    public void Scope_UnpairedSurrogates_AreNotReplacedByAnEncoder()
    {
        var first = new string((char)0xd800, 1);
        Assert.NotEqual(SqlServerBackplane.GetScope(first), SqlServerBackplane.GetScope(new string((char)0xd801, 1)));
        Assert.NotEqual(SqlServerBackplane.GetScope(first), SqlServerBackplane.GetScope("�"));
    }

    [Fact]
    public void Scope_LongPrefix_HasFixedSize()
    {
        Assert.Equal(32, SqlServerBackplane.GetScope(new string('x', 10_000)).Length);
    }
}
