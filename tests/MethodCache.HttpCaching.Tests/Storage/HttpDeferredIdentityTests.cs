using MethodCache.Core.Runtime.Http;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.HttpCaching.Tests.Storage;

public class HttpDeferredIdentityTests
{
    [Fact]
    public async Task LongLocalKeys_DoNotDuplicateTargetsBeforeAdministration()
    {
        var time = new FakeTimeProvider();
        using var owner = new TestHttpStore(time);
        var storage = owner.ForClient("deferred-target", new HttpCacheOptions());
        var entry = new HttpCacheEntry { RequestUri = "https://example.test/a", Method = "GET", StoredAt = time.GetUtcNow(), Content = [1] };
        string[] keys = Enumerable.Range(0, 64).Select(i => "client:" + new string('x', 16384) + i + ":m:GET").ToArray();
        for (var i = 0; i < 32; i++)
            Assert.True(await storage.TrySetAsync("warm:" + i + ":m:GET", entry, TimeSpan.FromMinutes(1), default));
        var perKey = MeasurePublications(storage, entry, keys);
        Assert.True(perKey < 4096, $"Local publication allocates {perKey:F1} B per key; target text already belongs to the stored key.");
        var listed = owner.Core.DescribeEntries(null, null, 100);
        var expected = keys.Select(HttpResponseStore.Identity).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(keys.Length, listed.Count(info => expected.Contains(info.Key)));
        Assert.True(await owner.Core.RemoveObservationAsync(HttpResponseStore.Identity(keys[0]), default));
        Assert.Null(await storage.GetAsync(keys[0]));
    }

    private static double MeasurePublications(IHttpCacheStorage storage, HttpCacheEntry entry, string[] keys)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var key in keys)
        {
            var stored = storage.TrySetAsync(key, entry, TimeSpan.FromMinutes(1), default);
            if (!stored.IsCompletedSuccessfully || !stored.Result) throw new InvalidOperationException("Local publication must complete synchronously.");
        }
        return (GC.GetAllocatedBytesForCurrentThread() - before) / (double)keys.Length;
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var row in HttpStorageIdentityTests.Keys())
        {
            var key = "client:test:" + (string)row[0] + "|generation:g:m:GET";
            yield return [key, false];
            yield return [key, true];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task LocalAdministration_IdentitySurvivesReplacement_AndRemovesTheStoredRepresentation(string key, bool inspectBeforeReplacement)
    {
        var time = new FakeTimeProvider();
        using var owner = new TestHttpStore(time);
        var storage = owner.ForClient("identity-client", new HttpCacheOptions());
        var expected = HttpResponseStore.Identity(key);
        var first = new HttpCacheEntry { RequestUri = "https://example.test/a", Method = "GET", StoredAt = time.GetUtcNow(), Content = [1] };
        Assert.True(await storage.TrySetAsync(key, first, TimeSpan.FromSeconds(5), default));
        string? observedBefore = null;
        if (inspectBeforeReplacement)
        {
            observedBefore = Assert.Single(owner.Core.DescribeEntries(null, null, 10)).Key;
            Assert.Equal(expected, observedBefore);
        }

        time.Advance(TimeSpan.FromSeconds(1));
        var replacement = first with { StoredAt = time.GetUtcNow(), Content = new byte[2048] };
        Assert.True(await storage.TrySetAsync(key, replacement, TimeSpan.FromSeconds(20), default));
        var listed = Assert.Single(owner.Core.DescribeEntries(null, null, 10));
        Assert.Equal(expected, listed.Key);
        Assert.StartsWith("http:", listed.Key);
        Assert.Equal(69, listed.Key.Length);
        Assert.Equal(CacheEntryKind.Http, listed.Kind);
        Assert.Equal(CacheEntryStorage.LocalMemory, listed.Storage);
        Assert.Equal("identity-client", listed.Client);
        Assert.Equal(TimeSpan.Zero, listed.Age);
        Assert.Equal(TimeSpan.FromSeconds(20), listed.TimeToLive);
        Assert.Same(replacement, await storage.GetAsync(key));
        Assert.Same(listed.Key, Assert.Single(owner.Core.DescribeEntries(null, null, 10)).Key);
        if (observedBefore != null) Assert.Same(observedBefore, listed.Key);

        Assert.True(await owner.Core.RemoveObservationAsync(expected, default));
        Assert.Null(await storage.GetAsync(key));
        Assert.Empty(owner.Core.DescribeEntries(null, null, 10));
    }
}
