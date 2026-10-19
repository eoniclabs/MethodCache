using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Docs.Tests;

public class ManualLookupTests
{
    [Fact]
    public async Task ManualLookup_RefreshWindow_ReportsCachedNull()
    {
        var time = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddMethodCache();
        services.Configure<MethodCacheMemoryOptions>(options => { options.CacheNullValues = true; });
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheManager>();
        var calls = 0;
        await cache.GetOrCreateAsync<string?>("entry", _ => { calls++; return ValueTask.FromResult<string?>(null); }, options => options.WithDuration(TimeSpan.FromMinutes(1)).WithRefreshAhead(TimeSpan.FromSeconds(10)));
        time.Advance(TimeSpan.FromSeconds(51));
        var lookup = await cache.TryGetAsync<string?>("entry");
        Assert.True(lookup.Found);
        Assert.Null(lookup.Value);
        Assert.Equal(1, calls);
    }
}
