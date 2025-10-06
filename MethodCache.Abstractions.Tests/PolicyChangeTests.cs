using System;
using System.Collections.Generic;
using MethodCache.Abstractions.Policies;
using MethodCache.Abstractions.Resolution;

namespace MethodCache.Abstractions.Tests;

public class PolicyChangeTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var snapshot = CachePolicy.Empty with { Duration = TimeSpan.FromSeconds(5) };
        var delta = new CachePolicyDelta(CachePolicyFields.Duration, CachePolicyFields.None, snapshot);
        var timestamp = DateTimeOffset.UtcNow;
        var metadata = new Dictionary<string, string?> { ["hint"] = "test" };

        var change = new PolicyChange("source", "method", delta, PolicyChangeReason.Added, timestamp, metadata);

        Assert.Equal("source", change.SourceId);
        Assert.Equal("method", change.MethodId);
        Assert.Equal(delta, change.Delta);
        Assert.Equal(PolicyChangeReason.Added, change.Reason);
        Assert.Equal(timestamp, change.Timestamp);
        Assert.Equal(metadata, change.Metadata);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_Throws_WhenSourceInvalid(string? source)
    {
        var delta = new CachePolicyDelta(CachePolicyFields.None, CachePolicyFields.None, CachePolicy.Empty);

        Assert.Throws<ArgumentException>(() => new PolicyChange(source!, "method", delta, PolicyChangeReason.Added, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_Throws_WhenMethodInvalid(string? method)
    {
        var delta = new CachePolicyDelta(CachePolicyFields.None, CachePolicyFields.None, CachePolicy.Empty);

        Assert.Throws<ArgumentException>(() => new PolicyChange("source", method!, delta, PolicyChangeReason.Added, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Constructor_Throws_WhenDeltaNull()
    {
        Assert.Throws<ArgumentNullException>(() => new PolicyChange("source", "method", null!, PolicyChangeReason.Added, DateTimeOffset.UtcNow));
    }
}
