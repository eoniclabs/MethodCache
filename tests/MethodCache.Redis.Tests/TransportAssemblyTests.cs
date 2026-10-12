using Xunit;

namespace MethodCache.Redis.Tests;

public class TransportAssemblyTests
{
    [Fact]
    public void Backplane_WireTextImplementation_IsOwnedByAddOnAssembly()
    {
        var type = typeof(RedisBackplane).Assembly.GetType("MethodCache.Infrastructure.LosslessWireText");
        Assert.NotNull(type);
        Assert.False(type.IsPublic);
    }
}
