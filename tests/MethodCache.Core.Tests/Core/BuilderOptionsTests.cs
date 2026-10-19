using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MethodCache.Core.Tests.Core;

public class BuilderOptionsTests
{
    [Fact]
    public void DistributedAndTelemetry_ConfigureTheirOptions()
    {
        var services = new ServiceCollection();
        services.AddMethodCache(mc => mc
            .Distributed(o => o.KeyPrefix = "orders-api:")
            .Telemetry(o => o.RecordCacheKeys = true));
        using var provider = services.BuildServiceProvider();

        Assert.Equal("orders-api:", provider.GetRequiredService<IOptions<DistributedCacheOptions>>().Value.KeyPrefix);
        Assert.True(provider.GetRequiredService<IOptions<MethodCacheTelemetryOptions>>().Value.RecordCacheKeys);
    }

    [Fact]
    public void DistributedAndTelemetry_RejectNull()
    {
        var services = new ServiceCollection();
        services.AddMethodCache(mc =>
        {
            Assert.Throws<ArgumentNullException>(() => mc.Distributed(null!));
            Assert.Throws<ArgumentNullException>(() => mc.Telemetry(null!));
        });
    }

    [Fact]
    public async Task StateFactory_PassesStateAndToken()
    {
        using var cancellation = new CancellationTokenSource();
        foreach (var cache in new ICacheManager[] { new NoOpCacheManager(), NewMemoryManager() })
        {
            var value = await cache.GetOrCreateAsync("state", 20, static (state, token) =>
            {
                Assert.False(token.IsCancellationRequested);
                return ValueTask.FromResult(state + 22);
            }, cancellationToken: cancellation.Token);
            Assert.Equal(42, value);
            (cache as IDisposable)?.Dispose();
        }
    }

    private static InMemoryCacheManager NewMemoryManager()
        => new(Microsoft.Extensions.Options.Options.Create(new MethodCacheMemoryOptions { EnableBackgroundCleanup = false }));
}
