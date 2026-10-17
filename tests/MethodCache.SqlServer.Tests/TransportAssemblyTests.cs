using Xunit;

namespace MethodCache.SqlServer.Tests;

public class TransportAssemblyTests
{
    [Fact]
    public void Backplane_WireTextImplementation_UsesMatchingCoreInternals()
    {
        Assert.Null(typeof(SqlServerBackplane).Assembly.GetType("MethodCache.Infrastructure.LosslessWireText"));
        var type = typeof(ICacheManager).Assembly.GetType("MethodCache.Infrastructure.LosslessWireText");
        Assert.NotNull(type);
        Assert.False(type.IsPublic);
    }
}
