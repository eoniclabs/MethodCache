using MethodCache.Extensibility;
using Xunit;

namespace MethodCache.Core.Tests.Infrastructure;

public class NoOpCacheManagerTests
{
    [Fact]
    public async Task EveryCall_RunsTheFactory_AndEveryLookupMisses()
    {
        ICacheManager manager = new NoOpCacheManager();
        var calls = 0;
        await manager.GetOrCreateAsync("key", _ => ValueTask.FromResult(++calls));
        await manager.GetOrCreateAsync("key", _ => ValueTask.FromResult(++calls));
        Assert.Equal(2, calls);
        Assert.False((await manager.TryGetAsync<int>("key")).Found);
    }

    // As the cache does, so a test with NoOpCacheManager finds the mistake before production
    [Fact]
    public async Task CacheWhenOrDurationForAnotherResultType_Throws_BeforeTheFactoryRuns()
    {
        ICacheManager manager = new NoOpCacheManager();
        var calls = 0;

        var cacheWhen = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.GetOrCreateAsync("key", _ => ValueTask.FromResult(++calls), o => o.CacheWhen<long?>(value => value > 0)));
        var durationFor = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.GetOrCreateAsync("key", _ => ValueTask.FromResult(++calls), o => o.DurationFor<string>(_ => TimeSpan.FromMinutes(1))));

        Assert.Contains("CacheWhen<long?> can't check the int result of key", cacheWhen.Message, StringComparison.Ordinal);
        Assert.Contains("DurationFor<string> can't check the int result", durationFor.Message, StringComparison.Ordinal);
        Assert.Equal(0, calls);
    }
}
