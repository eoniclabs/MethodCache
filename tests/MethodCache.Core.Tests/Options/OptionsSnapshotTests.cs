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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task AddTags_BlankTag_ThrowsLikeInvalidateTagsAsync(string? tag)
    {
        var builder = new CacheEntryOptions.Builder().AddTags("kept");
        using var cache = new MethodCache.Extensibility.InMemoryCacheManager();

        var added = Assert.Throws<ArgumentException>(() => builder.AddTags("valid", tag!));
        var invalidated = await Assert.ThrowsAsync<ArgumentException>(() => cache.InvalidateTagsAsync([tag!], CancellationToken.None).AsTask());

        Assert.Equal(invalidated.GetType(), added.GetType());
        Assert.Equal(new[] { "kept" }, builder.Build().Tags);
    }

    [Fact]
    public void AddTags_NullArray_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(() => new CacheEntryOptions.Builder().AddTags(null!));
}
