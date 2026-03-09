using System.Threading.Tasks;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Configuration.Surfaces.Runtime;
using MethodCache.Core.Options;
using MethodCache.Core.PolicyPipeline.Model;
using Xunit;

namespace MethodCache.Core.Tests.Configuration;

public class RuntimeCacheConfiguratorTests
{
    private interface IRuntimeDocService
    {
        Task<string> GetAsync(int id);
        Task<string> SearchAsync(string query);
    }

    [Fact]
    public async Task UpsertAsync_WithPolicyBuilder_StoresPolicy()
    {
        var store = new RuntimePolicyStore();
        var configurator = new RuntimeCacheConfigurator(store);

        await configurator.UpsertAsync("Sample.Method", builder =>
        {
            builder.WithDuration(System.TimeSpan.FromMinutes(2));
            builder.AddTag("runtime");
            builder.RequireIdempotent();
        });

        var snapshots = store.GetSnapshots();
        var snapshot = Assert.Single(snapshots);
        Assert.Equal("Sample.Method", snapshot.MethodId);
        Assert.Equal(System.TimeSpan.FromMinutes(2), snapshot.Policy.Duration);
        Assert.Contains("runtime", snapshot.Policy.Tags);
        Assert.True(snapshot.Policy.RequireIdempotent);
        var fields = CachePolicyMapper.DetectFields(snapshot.Policy);
        Assert.Equal(CachePolicyFields.Duration |
                     CachePolicyFields.Tags |
                     CachePolicyFields.RequireIdempotent,
            fields);
    }

    [Fact]
    public async Task UpsertAsync_WithEntryOptionsBuilder_StoresPolicy()
    {
        var store = new RuntimePolicyStore();
        var configurator = new RuntimeCacheConfigurator(store);

        await configurator.UpsertAsync("Sample.Entry", builder =>
        {
            builder.WithDuration(System.TimeSpan.FromMinutes(3));
            builder.WithTags("runtime", "docs");
        });

        var snapshots = store.GetSnapshots();
        var snapshot = Assert.Single(snapshots);
        Assert.Equal("Sample.Entry", snapshot.MethodId);
        Assert.Equal(System.TimeSpan.FromMinutes(3), snapshot.Policy.Duration);
        Assert.Contains("runtime", snapshot.Policy.Tags);
        Assert.Contains("docs", snapshot.Policy.Tags);
    }

    [Fact]
    public async Task ApplyAsync_WithFluentConfiguration_StoresMethodOverrides()
    {
        var store = new RuntimePolicyStore();
        var configurator = new RuntimeCacheConfigurator(store);

        await configurator.ApplyAsync(fluent =>
        {
            fluent.ForService<IRuntimeDocService>()
                .Method(x => x.GetAsync(default))
                .Configure(options => options.WithDuration(System.TimeSpan.FromSeconds(30)).WithTags("runtime", "users"));

            fluent.ForService<IRuntimeDocService>()
                .Method(x => x.SearchAsync(default!))
                .Configure(options => options.WithDuration(System.TimeSpan.FromMinutes(2)));
        });

        var snapshots = store.GetSnapshots();
        Assert.Equal(2, snapshots.Count);

        var typeName = (typeof(IRuntimeDocService).FullName ?? nameof(IRuntimeDocService)).Replace('+', '.');
        var getMethodId = $"{typeName}.{nameof(IRuntimeDocService.GetAsync)}";
        var searchMethodId = $"{typeName}.{nameof(IRuntimeDocService.SearchAsync)}";

        var getSnapshot = Assert.Single(snapshots, s => s.MethodId == getMethodId);
        Assert.Equal(System.TimeSpan.FromSeconds(30), getSnapshot.Policy.Duration);
        Assert.Contains("runtime", getSnapshot.Policy.Tags);

        var searchSnapshot = Assert.Single(snapshots, s => s.MethodId == searchMethodId);
        Assert.Equal(System.TimeSpan.FromMinutes(2), searchSnapshot.Policy.Duration);
    }

    [Fact]
    public async Task RemoveAsync_RemovesOnlyRequestedOverride()
    {
        var store = new RuntimePolicyStore();
        var configurator = new RuntimeCacheConfigurator(store);

        await configurator.UpsertAsync("Method.One", (CacheEntryOptions.Builder builder) => builder.WithDuration(System.TimeSpan.FromMinutes(1)));
        await configurator.UpsertAsync("Method.Two", (CacheEntryOptions.Builder builder) => builder.WithDuration(System.TimeSpan.FromMinutes(2)));

        await configurator.RemoveAsync("Method.One");

        var snapshots = store.GetSnapshots();
        var remaining = Assert.Single(snapshots);
        Assert.Equal("Method.Two", remaining.MethodId);
    }

    [Fact]
    public async Task ClearAsync_RemovesAllOverrides()
    {
        var store = new RuntimePolicyStore();
        var configurator = new RuntimeCacheConfigurator(store);

        await configurator.UpsertAsync("Method.One", (CacheEntryOptions.Builder builder) => builder.WithDuration(System.TimeSpan.FromMinutes(1)));
        await configurator.UpsertAsync("Method.Two", (CacheEntryOptions.Builder builder) => builder.WithDuration(System.TimeSpan.FromMinutes(2)));

        await configurator.ClearAsync();

        Assert.Empty(store.GetSnapshots());
    }
}
