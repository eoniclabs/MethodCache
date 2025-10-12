using System;
using System.Linq;
using System.Threading.Tasks;
using MethodCache.Abstractions.Policies;
using MethodCache.Core;
using MethodCache.Core.PolicyPipeline.Sources;
using MethodCache.Core.Runtime.KeyGeneration;
using Xunit;

namespace MethodCache.Core.Tests.PolicyPipeline.Sources;

public class AttributePolicySourceTests
{
    [Fact]
    public async Task GetSnapshotAsync_BuildsPolicyFromCacheAttribute()
    {
        var source = new AttributePolicySource(typeof(AttributePolicySourceTests).Assembly);

        var snapshots = await source.GetSnapshotAsync();
        var snapshot = Assert.Single(snapshots, s => s.MethodId.EndsWith(nameof(ITestCacheService.GetUserAsync)));

        Assert.Equal(TimeSpan.FromMinutes(5), snapshot.Policy.Duration);
        Assert.Contains("users", snapshot.Policy.Tags);
        Assert.Equal(typeof(FastHashKeyGenerator), snapshot.Policy.KeyGeneratorType);
        Assert.Equal(2, snapshot.Policy.Version);
        Assert.True(snapshot.Policy.RequireIdempotent);
        Assert.Equal("service", snapshot.Policy.Metadata["group"]);

        var contribution = Assert.Single(snapshot.Policy.Provenance);
        Assert.Equal(CachePolicyFields.Duration |
                     CachePolicyFields.Tags |
                     CachePolicyFields.KeyGenerator |
                     CachePolicyFields.Version |
                     CachePolicyFields.RequireIdempotent |
                     CachePolicyFields.Metadata,
            contribution.Fields);
    }

    [Fact]
    public async Task GetSnapshotAsync_AppliesDefaultDurationWhenMissing()
    {
        var source = new AttributePolicySource(typeof(AttributePolicySourceTests).Assembly);

        var snapshots = await source.GetSnapshotAsync();
        var snapshot = Assert.Single(snapshots, s => s.MethodId.EndsWith(nameof(ITestDefaultService.FetchAsync)));

        Assert.Equal(TimeSpan.FromMinutes(15), snapshot.Policy.Duration);
        Assert.False(snapshot.Policy.RequireIdempotent.HasValue);
    }

    private interface ITestCacheService
    {
        [Cache("service", Duration = "00:05:00", Tags = new[] { "users", "profiles" }, Version = 2, RequireIdempotent = true, KeyGeneratorType = typeof(FastHashKeyGenerator))]
        Task<int> GetUserAsync(int id);
    }

    private interface ITestDefaultService
    {
        [Cache]
        Task<string> FetchAsync();
    }
}
