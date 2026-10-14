using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using NSubstitute;
using Xunit;

namespace MethodCache.Core.Tests.Infrastructure;

public class NoOpCacheManagerMetricsTests
{
    [Fact]
    public async Task EveryCall_IsReportedAsAMiss()
    {
        var metrics = Substitute.For<ICacheMetricsProvider>();
        var manager = new NoOpCacheManager(metrics);
        var policy = CacheRuntimePolicy.Empty("NoOp");

        await manager.GetOrCreateFastAsync("key", "NoOp", () => Task.FromResult(1), policy);
        await manager.GetOrCreateFastAsync("key", "NoOp", () => Task.FromResult(1), policy);

        metrics.Received(2).CacheMiss("NoOp");
        metrics.DidNotReceive().CacheHit(Arg.Any<string>());
        metrics.Received(2).CacheLatency("NoOp", Arg.Any<TimeSpan>());
    }
}
