using System.Threading.Tasks;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Configuration.Policies;
using MethodCache.Core.Configuration.Runtime;
using Xunit;

namespace MethodCache.Core.Tests.Configuration;

public class RuntimeCacheConfiguratorTests
{
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
}
