using System;
using MethodCache.Policies;
using Xunit;

namespace MethodCache.Core.Tests.Abstractions;

public class CachePolicyDeltaTests
{
    [Fact]
    public void IsEmpty_ReturnsTrue_WhenNoChanges()
    {
        var delta = new CachePolicyDelta(CachePolicyFields.None, CachePolicyFields.None, CachePolicy.Empty);

        Assert.True(delta.IsEmpty);
    }

    [Theory]
    [InlineData((int)CachePolicyFields.Duration, (int)CachePolicyFields.None)]
    [InlineData((int)CachePolicyFields.None, (int)CachePolicyFields.Duration)]
    public void IsEmpty_ReturnsFalse_WhenChangesPresent(int setMaskValue, int clearMaskValue)
    {
        var setMask = (CachePolicyFields)setMaskValue;
        var clearMask = (CachePolicyFields)clearMaskValue;
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
