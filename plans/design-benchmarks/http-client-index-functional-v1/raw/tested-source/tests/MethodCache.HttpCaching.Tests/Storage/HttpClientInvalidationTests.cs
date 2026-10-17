using MethodCache.Core.Runtime.Http;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.HttpCaching.Tests.Storage;

public class HttpClientInvalidationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientTagInvalidation_RemovesClientAndExplicitTagMatches_PreservesOtherClients(bool selector)
    {
        var time = new FakeTimeProvider();
        using var owner = new TestHttpStore(time);
        const string client = "client:*é\\\0";
        var tag = "http:" + MethodCacheKeys.Part(client);
        var first = owner.ForClient(client, new HttpCacheOptions());
        var matchingOptions = new HttpCacheOptions();
        matchingOptions.Tags.Add(tag);
        var matching = owner.ForClient("explicit-match", matchingOptions);
        var untouched = owner.ForClient("another-client", new HttpCacheOptions());
        var entry = new HttpCacheEntry
        {
            RequestUri = "https://example.test/a", Method = "GET", StoredAt = time.GetUtcNow(),
            Content = [1, 2, 3], VariantGeneration = selector ? "selector" : null
        };
        for (var i = 0; i < 16; i++)
        {
            Assert.True(await first.TrySetAsync($"first:{i}:m:GET", entry, TimeSpan.FromMinutes(1), default));
            Assert.True(await matching.TrySetAsync($"match:{i}:m:GET", entry, TimeSpan.FromMinutes(1), default));
            Assert.True(await untouched.TrySetAsync($"other:{i}:m:GET", entry, TimeSpan.FromMinutes(1), default));
        }
        var before = owner.Core.Observe().LocalHttpBytes;

        await owner.Core.InvalidateTagsAsync([tag], default);

        for (var i = 0; i < 16; i++)
        {
            Assert.Null(await first.GetAsync($"first:{i}:m:GET"));
            Assert.Null(await matching.GetAsync($"match:{i}:m:GET"));
            Assert.Same(entry, await untouched.GetAsync($"other:{i}:m:GET"));
        }
        var remaining = Enumerable.Range(0, 16)
            .Sum(i => Assert.IsType<HttpStorageInfo>(owner.Core.FindLocalInfo($"other:{i}:m:GET")).Cost);
        Assert.True(remaining > 0 && remaining < before);
        Assert.Equal(remaining, owner.Core.Observe().LocalHttpBytes);
        if (!selector)
        {
            var listed = owner.Core.DescribeEntries(null, null, 100);
            Assert.Equal(16, listed.Count);
            Assert.All(listed, info => Assert.Equal("another-client", info.Client));
        }
        await owner.Core.InvalidateTagsAsync(["http:" + MethodCacheKeys.Part("another-client")], default);
        for (var i = 0; i < 16; i++) Assert.Null(await untouched.GetAsync($"other:{i}:m:GET"));
        Assert.Empty(owner.Core.DescribeEntries(null, null, 100));
        Assert.Equal(0, owner.Core.Observe().LocalHttpBytes);
    }
}
