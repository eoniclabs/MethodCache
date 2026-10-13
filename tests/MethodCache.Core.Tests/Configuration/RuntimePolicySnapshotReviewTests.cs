using MethodCache.Core.Configuration.Surfaces.Runtime;
using MethodCache.Core.PolicyPipeline.Resolution;
using MethodCache.Core.PolicyPipeline.Sources;
using MethodCache.Policies;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Configuration;

public class RuntimePolicySnapshotReviewTests
{
    [Fact]
    public void RuntimePolicy_Tags_OwnAnImmutableSnapshot()
    {
        var tags = new[] { "tenant-a" };
        var policy = CacheRuntimePolicy.Empty("M") with { Tags = tags };
        tags[0] = "tenant-b";
        Assert.Equal("tenant-a", policy.Tags.Single());
        Assert.Throws<NotSupportedException>(() => ((ICollection<string>)policy.Tags).Add("other"));
        Assert.Throws<ArgumentNullException>(() => policy with { Tags = null! });
        Assert.Throws<ArgumentNullException>(() => CachePolicy.Empty with { Tags = null! });
        Assert.Empty((policy with { Tags = [] }).Tags);
    }

    [Fact]
    public async Task Upsert_CapturesTagsWithoutLaterUnversionedMutations()
    {
        using var store = new RuntimePolicyStore(new FakeTimeProvider());
        await using var resolver = new PolicyResolver([new PolicySourceRegistration(new RuntimePolicySource(store), 100)]);
        var tags = new[] { "tenant-a" };
        await store.UpsertAsync("Svc.Get", CachePolicy.Empty with { Tags = tags }, CachePolicyFields.Tags);
        var snapshot = await resolver.ResolveAsync("Svc.Get");
        tags[0] = "tenant-b";
        var next = await resolver.ResolveAsync("Svc.Get");
        Assert.Equal(snapshot.Version, next.Version);
        Assert.Equal("tenant-a", snapshot.Policy.Tags.Single());
        Assert.Equal("tenant-a", next.Policy.Tags.Single());
    }
}
