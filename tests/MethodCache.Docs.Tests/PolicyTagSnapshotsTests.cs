using MethodCache.Infrastructure;
using MethodCache.Policies;
using Xunit;

namespace MethodCache.Docs.Tests;

public class PolicyTagSnapshotsTests
{
    [Fact]
    public void PolicyTags_InputMutation_DoesNotChangePublishedData()
    {
        var tags = new[] { "tenant-a" };
        var policy = CachePolicy.Empty with { Tags = tags };
        var runtime = CacheRuntimePolicy.FromPolicy("M", policy, CachePolicyFields.Tags);
        tags[0] = "tenant-b";
        Assert.Equal("tenant-a", policy.Tags.Single());
        Assert.Equal("tenant-a", runtime.Tags.Single());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)policy.Tags)[0] = "other");
    }
}
