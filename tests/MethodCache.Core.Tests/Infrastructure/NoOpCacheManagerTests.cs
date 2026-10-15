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
        await manager.GetOrCreateAsync("key", (_, _) => ValueTask.FromResult(++calls));
        await manager.GetOrCreateAsync("key", (_, _) => ValueTask.FromResult(++calls));
        Assert.Equal(2, calls);
        Assert.False((await manager.TryGetAsync<int>("key")).Found);
    }
}
