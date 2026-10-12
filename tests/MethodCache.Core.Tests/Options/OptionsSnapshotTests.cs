using System;
using MethodCache;
using Xunit;

namespace MethodCache.Core.Tests.Options;

public class OptionsSnapshotTests
{
    [Fact]
    public void Build_ReusedBuilder_DoesNotChangePreviousTagsOrCallbacks()
    {
        Action<CacheContext> first = _ => { };
        Action<CacheContext> second = _ => { };
        var builder = new CacheEntryOptions.Builder().WithTags("first").OnHit(first).OnMiss(first);
        var snapshot = builder.Build();

        builder.WithTags("second").OnHit(second).OnMiss(second);
        var next = builder.Build();

        Assert.Equal(new[] { "first" }, snapshot.Tags);
        Assert.Equal(new[] { first }, snapshot.OnHitCallbacks);
        Assert.Equal(new[] { first }, snapshot.OnMissCallbacks);
        Assert.Equal(new[] { "first", "second" }, next.Tags);
        Assert.Equal(new[] { first, second }, next.OnHitCallbacks);
        Assert.Equal(new[] { first, second }, next.OnMissCallbacks);
    }
}
