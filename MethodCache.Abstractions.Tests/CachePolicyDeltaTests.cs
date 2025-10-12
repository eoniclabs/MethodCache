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

    [Theory]
    [InlineData(CachePolicyFields.Duration, CachePolicyFields.None)]
    [InlineData(CachePolicyFields.None, CachePolicyFields.Duration)]
    public void IsEmpty_ReturnsFalse_WhenChangesPresent(CachePolicyFields setMask, CachePolicyFields clearMask)
    {
        var snapshot = CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(1) };
        var delta = new CachePolicyDelta(setMask, clearMask, snapshot);

        Assert.False(delta.IsEmpty);
    }

    [Fact]
    public void Constructor_Throws_WhenSnapshotNull()
    {
        Assert.Throws<ArgumentNullException>(() => new CachePolicyDelta(CachePolicyFields.None, CachePolicyFields.None, null!));
    }
}
