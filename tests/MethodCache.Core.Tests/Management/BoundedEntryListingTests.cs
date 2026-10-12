using System.Globalization;
using MethodCache.Core.Storage.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Management;

public class BoundedEntryListingTests
{
    [Fact]
    public async Task ListEntries_SmallLimitInLargeCache_AllocatesOnlyForSelectedEntries()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton<TimeProvider>(new FakeTimeProvider())
            .Configure<MethodCacheMemoryOptions>(options =>
            {
                options.MaxItems = 50_000;
                options.EnableStatistics = false;
                options.EnableBackgroundCleanup = false;
            })
            .AddMethodCache()
            .BuildServiceProvider();
        var memory = provider.GetRequiredService<IMemoryCache>();
        var admin = provider.GetRequiredService<ICacheAdministrator>();
        for (var i = 29_999; i >= 0; i--)
            await memory.SetAsync("key:" + i.ToString("D6", CultureInfo.InvariantCulture), i, TimeSpan.FromMinutes(1));

        _ = admin.ListEntries(limit: 5);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var selected = admin.ListEntries(limit: 5);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(Enumerable.Range(0, 5).Select(i => "key:" + i.ToString("D6", CultureInfo.InvariantCulture)), selected.Select(e => e.Key));
        Assert.InRange(allocated, 0, 16_384);
    }
}
