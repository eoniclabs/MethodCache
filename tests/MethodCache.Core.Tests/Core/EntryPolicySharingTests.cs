using System.Reflection;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Xunit;

namespace MethodCache.Core.Tests.Core;

/// <summary>
/// Entries whose settings are made for one call (a tag filled in from the call's arguments, options an application
/// builds) must not each leave something behind that every garbage collection has to walk.
/// </summary>
public class EntryPolicySharingTests
{
    [Fact]
    public async Task ParameterizedTag_EntryDoesNotRegisterItsOwnPolicy()
    {
        using var cache = new InMemoryCacheManager(Microsoft.Extensions.Options.Options.Create(new MethodCacheMemoryOptions { EnableBackgroundCleanup = false }));
        var method = new CacheRuntimePolicy { MethodId = "Users.Get", Duration = TimeSpan.FromMinutes(5), Tags = ["user:{id}", "users"] };

        var expanded = new List<CacheRuntimePolicy>();
        for (var id = 0; id < 50; id++)
        {
            var policy = CacheTagTemplates.Expand(method, ["id"], [(object?)id]);
            expanded.Add(policy);
            await cache.GetOrCreateFastAsync($"user-{id}", "Users.Get", () => Task.FromResult(id), policy);
        }

        Assert.All(expanded, policy => Assert.False(IsRegistered(policy)));
        Assert.True(IsRegistered(method));

        // The entries still carry their own filled-in tags and the method's shared ones
        await cache.InvalidateTagsAsync(["user:7"], CancellationToken.None);
        Assert.False((await cache.TryGetAsync<int>("user-7", null, CancellationToken.None)).Found);
        Assert.True((await cache.TryGetAsync<int>("user-8", null, CancellationToken.None)).Found);
        await cache.InvalidateTagsAsync(["users"], CancellationToken.None);
        Assert.False((await cache.TryGetAsync<int>("user-8", null, CancellationToken.None)).Found);
    }

    [Fact]
    public void ExpandingAnExpandedPolicy_KeepsTheMethodsPolicyAsItsSource()
    {
        var method = new CacheRuntimePolicy { MethodId = "Users.Get", Tags = ["user:{id}"] };
        var once = CacheTagTemplates.Expand(method, ["id"], [(object?)1]);
        var twice = CacheTagTemplates.Expand(once with { Tags = ["user:{id}"] }, ["id"], [(object?)2]);

        Assert.Same(method, once.Template);
        Assert.Same(method, twice.Template);
        Assert.Equal(["user:2"], twice.Tags);
    }

    [Fact]
    public async Task ManualOptions_BuildTheirSettingsOnceAndShareThem()
    {
        using var cache = new InMemoryCacheManager(Microsoft.Extensions.Options.Options.Create(new MethodCacheMemoryOptions { EnableBackgroundCleanup = false }));
        var options = new CacheEntryOptions.Builder().WithDuration(TimeSpan.FromMinutes(5)).AddTags("orders").Build();

        await cache.GetOrCreateAsync("order-1", _ => ValueTask.FromResult(1), options);
        var settings = options.ManualEntry;
        Assert.NotNull(settings);
        await cache.GetOrCreateAsync("order-2", _ => ValueTask.FromResult(2), options);
        await cache.SetAsync("order-3", 3, options);
        Assert.Same(settings, options.ManualEntry);

        await cache.InvalidateTagsAsync(["orders"], CancellationToken.None);
        Assert.False((await cache.TryGetAsync<int>("order-1", null, CancellationToken.None)).Found);
        Assert.False((await cache.TryGetAsync<int>("order-3", null, CancellationToken.None)).Found);
    }

    private static bool IsRegistered(CacheRuntimePolicy policy)
    {
        var table = typeof(InMemoryCacheManager).GetField("PolicyCache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var arguments = new object?[] { policy, null };
        return (bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, arguments)!;
    }
}
