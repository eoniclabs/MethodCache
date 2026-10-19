using System;
using MethodCache;
using Xunit;

namespace MethodCache.Core.Tests.Options;

public class OptionsSnapshotTests
{
    [Fact]
    public void Build_ReusedBuilder_DoesNotChangePreviousTags()
    {
        var builder = new CacheEntryOptions.Builder().AddTags("first");
        var snapshot = builder.Build();

        builder.AddTags("second");
        var next = builder.Build();

        Assert.Equal(new[] { "first" }, snapshot.Tags);
        Assert.Equal(new[] { "first", "second" }, next.Tags);
    }
}
