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

    [Fact]
    public void ZeroSlidingExpirationAndRefreshAhead_TurnThemOff_NegativeThrows()
    {
        var options = new CacheEntryOptions.Builder()
            .WithDuration(TimeSpan.FromMinutes(5))
            .WithSlidingExpiration(TimeSpan.Zero)
            .WithRefreshAhead(TimeSpan.Zero)
            .Build();

        Assert.Equal(TimeSpan.Zero, options.SlidingExpiration);
        Assert.Equal(TimeSpan.Zero, options.RefreshAhead);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CacheEntryOptions.Builder().WithSlidingExpiration(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CacheEntryOptions.Builder().WithRefreshAhead(TimeSpan.FromSeconds(-1)));
        // A refresh window still has to fit inside the sliding window when both are on
        Assert.Throws<InvalidOperationException>(() => new CacheEntryOptions.Builder().WithDuration(TimeSpan.FromMinutes(5))
            .WithSlidingExpiration(TimeSpan.FromSeconds(10)).WithRefreshAhead(TimeSpan.FromSeconds(20)).Build());
    }

    [Fact]
    public async Task CacheWhen_ForAnotherResultType_Throws()
    {
        using (var cache = NewMemoryManager())
        {
            var calls = 0;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await cache.GetOrCreateAsync(
                "mismatch", _ => { calls++; return ValueTask.FromResult(42); }, o => o.CacheWhen<string>(text => text.Length > 0)));
            Assert.Contains("CacheWhen<String>", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, calls);
        }

        var options = new CacheEntryOptions.Builder().CacheWhen<string>(text => text.Length > 0).Build();
        Assert.Throws<InvalidOperationException>(() => options.CanCache(42));
        Assert.True(new CacheEntryOptions.Builder().CacheWhen<object>(value => value is not null).Build().CanCache(42));
    }

    private static InMemoryCacheManager NewMemoryManager()
        => new(Microsoft.Extensions.Options.Options.Create(new MethodCacheMemoryOptions { EnableBackgroundCleanup = false }));
}
