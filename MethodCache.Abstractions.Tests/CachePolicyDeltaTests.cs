using System;
using MethodCache.Abstractions.Policies;
using MethodCache.Abstractions.Resolution;

namespace MethodCache.Abstractions.Tests;

public class CachePolicyDeltaTests
{
    [Fact]
    public void IsEmpty_ReturnsTrue_WhenNoChanges()
    {
        var delta = new CachePolicyDelta(CachePolicyFields.None, CachePolicyFields.None, CachePolicy.Empty);

        Assert.True(delta.IsEmpty);
    }

    [Fact]
    public void IsEmpty_ReturnsFalse_WhenSetMaskPresent()
    {
        var snapshot = CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(1) };
        var delta = new CachePolicyDelta(CachePolicyFields.Duration, CachePolicyFields.None, snapshot);

        Assert.False(delta.IsEmpty);
    }

    [Fact]
    public void Constructor_Throws_WhenSnapshotNull()
    {
        Assert.Throws<ArgumentNullException>(() => new CachePolicyDelta(CachePolicyFields.None, CachePolicyFields.None, null!));
    }
}
