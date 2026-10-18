using Xunit;

namespace MethodCache.Redis.Tests;

public class TransportAssemblyTests
{
    [Fact]
    public void Backplane_WireTextImplementation_UsesMatchingCoreInternals()
    {
        Assert.Null(typeof(RedisBackplane).Assembly.GetType("MethodCache.Infrastructure.LosslessWireText"));
        var type = typeof(ICacheManager).Assembly.GetType("MethodCache.Infrastructure.LosslessWireText");
        Assert.NotNull(type);
        Assert.False(type.IsPublic);
    }
}
