using Xunit;

namespace MethodCache.SqlServer.Tests;

public class TransportAssemblyTests
{
    [Fact]
    public void Backplane_WireTextImplementation_IsOwnedByAddOnAssembly()
    {
        var type = typeof(SqlServerBackplane).Assembly.GetType("MethodCache.Infrastructure.LosslessWireText");
        Assert.NotNull(type);
        Assert.False(type.IsPublic);
    }
}
