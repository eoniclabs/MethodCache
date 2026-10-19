using MethodCache.Extensibility;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Core;

public class ManualLookupReviewTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Lookup_ReturnsAFreshValueRegardlessOfFastPathOrRefreshWindow(bool fastPath, bool refreshDue)
    {
        var time = new FakeTimeProvider();
        using var manager = new InMemoryCacheManager(Microsoft.Extensions.Options.Options.Create(new MethodCacheMemoryOptions { EnableFastPath = fastPath, EnableBackgroundCleanup = false }), time);
        await manager.GetOrCreateAsync("key", _ => ValueTask.FromResult(7), options =>
        {
            options.WithDuration(TimeSpan.FromSeconds(60));
            if (refreshDue) options.WithRefreshAhead(TimeSpan.FromSeconds(10));
        });
        time.Advance(TimeSpan.FromSeconds(51));
        Assert.Equal(7, await manager.GetAsync<int>("key"));
        var lookup = await manager.TryGetAsync<int>("key");
        Assert.True(lookup.Found);
        Assert.Equal(7, lookup.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task Lookup_CachedNull_PreservesPresenceAndVersion(int? version)
    {
        var time = new FakeTimeProvider();
        using var manager = new InMemoryCacheManager(Microsoft.Extensions.Options.Options.Create(new MethodCacheMemoryOptions { EnableFastPath = false, CacheNullValues = true, EnableBackgroundCleanup = false }), time);
        await manager.GetOrCreateAsync<string?>("null", _ => ValueTask.FromResult<string?>(null), options =>
        {
            options.WithDuration(TimeSpan.FromMinutes(1));
            if (version.HasValue) options.WithVersion(version.Value);
        });

        var hit = await manager.TryGetAsync<string?>("null", version);
        Assert.True(hit.Found);
        Assert.Null(hit.Value);
        Assert.False((await manager.TryGetAsync<string?>("missing", version)).Found);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False((await manager.TryGetAsync<string?>("null", version)).Found);
    }
}
