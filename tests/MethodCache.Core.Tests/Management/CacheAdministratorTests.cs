using System;
using System.Linq;
using System.Threading.Tasks;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Management;

public class CacheAdministratorTests
{
    public interface IAdminDocService
    {
        Task<string> GetAsync(int id);
    }

    private static readonly CacheRuntimePolicy TaggedPolicy = CacheRuntimePolicy.FromPolicy(
        "Docs",
        CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(5), Tags = new[] { "docs" } },
        CachePolicyFields.Duration | CachePolicyFields.Tags);

    private static ServiceProvider BuildProvider(TimeProvider? time = null) =>
        new ServiceCollection()
            .AddSingleton(time ?? TimeProvider.System)
            .AddMethodCache(config => config.Method((IAdminDocService x) => x.GetAsync(default))
                .Configure(options => options.WithDuration(TimeSpan.FromSeconds(30)).WithTags("docs")))
            .BuildServiceProvider();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailSafeThrottle_PhysicalExpiry_HidesEntryAndReportsRemovalAsNotLive(bool sliding)
    {
        var time = new FakeTimeProvider();
        await using var provider = BuildProvider(time);
        var cache = provider.GetRequiredService<ICacheManager>();
        var admin = provider.GetRequiredService<ICacheAdministrator>();
        var policy = TaggedPolicy with
        {
            Duration = TimeSpan.FromSeconds(1),
            FailSafe = TimeSpan.FromSeconds(1),
            FailSafeRetryAfter = TimeSpan.FromSeconds(5),
            SlidingExpiration = sliding ? TimeSpan.FromSeconds(1) : null
        };
        await cache.GetOrCreateFastAsync("key", "Docs", () => Task.FromResult(1), policy);
        time.Advance(TimeSpan.FromSeconds(1));
        await cache.GetOrCreateFastAsync<int>("key", "Docs", () => Task.FromException<int>(new InvalidOperationException()), policy);
        time.Advance(TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1));
        Assert.NotNull(admin.GetEntry("key"));
        Assert.InRange(admin.GetEntry("key")!.TimeToLive!.Value, TimeSpan.Zero, TimeSpan.FromTicks(1));
        time.Advance(TimeSpan.FromTicks(1));
        Assert.Null(admin.GetEntry("key"));
        Assert.Empty(admin.ListEntries());
        Assert.False(await admin.RemoveEntryAsync("key"));
    }

    [Fact]
    public async Task ListEntries_ReturnsLiveEntriesOrderedByKey_UpToTheLimit()
    {
        await using var provider = BuildProvider();
        var cache = provider.GetRequiredService<ICacheManager>();
        var admin = provider.GetRequiredService<ICacheAdministrator>();

        foreach (var id in new[] { 3, 1, 2 })
        {
            await cache.GetOrCreateFastAsync($"docs:{id}", "Docs", () => Task.FromResult($"doc-{id}"), TaggedPolicy);
        }

        var entries = admin.ListEntries(limit: 2);

        Assert.Equal(new[] { "docs:1", "docs:2" }, entries.Select(e => e.Key));
        Assert.All(entries, e => Assert.Equal(typeof(string).FullName, e.ValueType));
        Assert.All(entries, e => Assert.InRange(e.TimeToLive!.Value, TimeSpan.Zero, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task GetEntry_DescribesOneEntry_OrReturnsNullWhenMissing()
    {
        await using var provider = BuildProvider();
        var memory = provider.GetRequiredService<IMemoryCache>();
        var admin = provider.GetRequiredService<ICacheAdministrator>();

        await memory.SetAsync("answer", 42, TimeSpan.FromMinutes(1));

        var entry = admin.GetEntry("answer");

        Assert.NotNull(entry);
        Assert.Equal(typeof(int).FullName, entry.ValueType);
        Assert.InRange(entry.TimeToLive!.Value, TimeSpan.FromSeconds(50), TimeSpan.FromMinutes(1));
        Assert.Empty(entry.Tags);
        Assert.Null(admin.GetEntry("missing"));
    }

    [Fact]
    public async Task InvalidateTagsAndRemove_ChangeTheCache()
    {
        await using var provider = BuildProvider();
        var cache = provider.GetRequiredService<ICacheManager>();
        var admin = provider.GetRequiredService<ICacheAdministrator>();
        await cache.GetOrCreateFastAsync("docs:1", "Docs", () => Task.FromResult("a"), TaggedPolicy);
        await cache.GetOrCreateFastAsync("docs:2", "Docs", () => Task.FromResult("b"), TaggedPolicy);
        await provider.GetRequiredService<IMemoryCache>().SetAsync("other", "c", TimeSpan.FromMinutes(1));

        Assert.True(await admin.RemoveEntryAsync("other"));
        await admin.InvalidateTagsAsync(new[] { "docs" });

        Assert.Empty(admin.ListEntries());
        Assert.Equal(0, (await admin.GetStatsAsync()).LocalMethodEntries);
    }

    [Fact]
    public async Task RemoveEntry_ReturnsFalse_ForAnEntryThatHasExpired()
    {
        var time = new FakeTimeProvider();
        await using var provider = BuildProvider(time);
        var memory = provider.GetRequiredService<IMemoryCache>();
        var admin = provider.GetRequiredService<ICacheAdministrator>();

        await memory.SetAsync("short", "value", TimeSpan.FromMilliseconds(50));
        Assert.NotNull(admin.GetEntry("short"));
        time.Advance(TimeSpan.FromMilliseconds(50));

        Assert.Null(admin.GetEntry("short"));
        Assert.False(await admin.RemoveEntryAsync("short"));
    }

    [Fact]
    public async Task EntryTags_AreASnapshot()
    {
        await using var provider = BuildProvider();
        var cache = provider.GetRequiredService<ICacheManager>();
        var admin = provider.GetRequiredService<ICacheAdministrator>();
        await cache.GetOrCreateFastAsync("docs:1", "Docs", () => Task.FromResult("doc-1"), TaggedPolicy);

        ((string[])admin.GetEntry("docs:1")!.Tags)[0] = "changed";

        Assert.Equal(new[] { "docs" }, admin.GetEntry("docs:1")!.Tags);
        Assert.Single(admin.ListEntries(tag: "docs"));
    }

    [Fact]
    public async Task GetMethods_ReportsTheEffectivePolicyAndItsSources()
    {
        await using var provider = BuildProvider();
        var admin = provider.GetRequiredService<ICacheAdministrator>();
        var methodId = global::MethodCache.Core.PolicyPipeline.Model.MethodIds.For(typeof(IAdminDocService), nameof(IAdminDocService.GetAsync));

        var method = Assert.Single(admin.GetMethods(), m => m.MethodId == methodId);

        Assert.Equal(TimeSpan.FromSeconds(30), method.Duration);
        Assert.Contains("docs", method.Tags);
        Assert.NotEmpty(method.Sources);
    }
}
