using System.Net;
using System.Text.Json;
using MethodCache.Core.Runtime.Distributed;
using MethodCache.Extensibility;
using MethodCache.HttpCaching.Internal;
using MethodCache.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class HttpObservationAcceptanceTests
{
    [Fact]
    public async Task L2Observations_AreBoundedBodyFreeHistoricalAndExpire_WithoutBackendInventory()
    {
        var time = new FakeTimeProvider(); var backend = HttpEpochTestCache.Create(time);
        using var store = new TestHttpStore(time, backend);
        var view = store.ForClient("api", new());
        var deadline = TimeSpan.FromMinutes(1);
        for (var i = 0; i < 1100; i++) await view.SetAsync("key:" + i, Entry(time), deadline);
        var stats = store.Core.Observe();
        Assert.Equal(0, stats.LocalHttpEntries); Assert.Equal(0, stats.LocalHttpBytes);
        Assert.Equal(1024, stats.ObservedDistributedEntries);
        var observed = store.Core.DescribeEntries(null, null, 2000);
        Assert.Equal(1024, observed.Count);
        Assert.All(observed, info =>
        {
            Assert.Equal(CacheEntryKind.Http, info.Kind); Assert.Equal(CacheEntryStorage.ObservedDistributed, info.Storage);
            Assert.Equal(69, info.StoredKey.Length); Assert.Contains("Historical", info.Description);
        });
        var text = JsonSerializer.Serialize(observed);
        foreach (var secret in new[] { "user-canary", "password-canary", "query-canary", "cookie-canary", "body-canary", "credential-canary" }) Assert.DoesNotContain(secret, text);
        var known = store.Core.List().First();
        await backend.RemoveAsync("methodcache:http:e2:" + known.UrlTarget);
        Assert.NotNull(store.Core.Find(known.Id)); // A historical observation can outlive a remote deletion.
        Assert.False(await store.Core.RemoveObservationAsync("http:" + new string('0', 64), default));
        using var restarted = new TestHttpStore(time, backend);
        Assert.Empty(restarted.Core.List()); Assert.Equal(0, restarted.Core.Observe().ObservedDistributedEntries);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Empty(store.Core.List()); Assert.Equal(0, store.Core.Observe().ObservedDistributedEntries);
    }

    [Fact]
    public async Task L2CoreOnlyTagAndClear_RetireAllHttpFormatsWithoutBackplaneDelivery()
    {
        var time = new FakeTimeProvider(); var backend = HttpEpochTestCache.Create(time);
        using var first = new TestHttpStore(time, backend, format: 2);
        using var future = new TestHttpStore(time, backend, format: 3);
        var options = new HttpCacheOptions(); options.Tags.Add("orders");
        var a = first.ForClient("api", options); var b = future.ForClient("api", options);
        const string target = "url";
        var old = await a.GetGenerationAsync(target); Assert.Equal(old, await b.GetGenerationAsync(target));
        await a.SetAsync(HttpCacheKeys.ForGeneration(target, old), Entry(time), TimeSpan.FromHours(1));
        await b.SetAsync(HttpCacheKeys.ForGeneration(target, old), Entry(time), TimeSpan.FromHours(1));
        Assert.NotNull(await a.GetAsync(HttpCacheKeys.ForGeneration(target, old)));
        Assert.NotNull(await b.GetAsync(HttpCacheKeys.ForGeneration(target, old)));
        // This node never constructs an HTTP store or establishes a backplane subscription.
        using var coreOnly = new DistributedCacheLayer(backend, new JsonCacheSerializer(),
            new DistributedCacheOptions { ClockSkewTolerance = TimeSpan.Zero }, timeProvider: time);
        await coreOnly.InvalidateTagsAsync(["orders"]);
        var afterTag = await a.GetGenerationAsync(target);
        Assert.NotEqual(old, afterTag); Assert.Equal(afterTag, await b.GetGenerationAsync(target));
        Assert.Null(await a.GetAsync(HttpCacheKeys.ForGeneration(target, afterTag)));
        Assert.Null(await b.GetAsync(HttpCacheKeys.ForGeneration(target, afterTag)));
        await coreOnly.ClearAsync();
        var afterClear = await b.GetGenerationAsync(target);
        Assert.NotEqual(afterTag, afterClear); Assert.Equal(afterClear, await a.GetGenerationAsync(target));
    }

    [Fact]
    public async Task ObservationRemoval_RetiresUrlNamespaceAcrossFormats()
    {
        var time = new FakeTimeProvider(); var backend = HttpEpochTestCache.Create(time);
        using var first = new TestHttpStore(time, backend, format: 2);
        using var future = new TestHttpStore(time, backend, format: 3);
        var a = first.ForClient("api", new()); var b = future.ForClient("api", new());
        var old = await a.GetGenerationAsync("url");
        await a.SetAsync(HttpCacheKeys.ForGeneration("url", old), Entry(time), TimeSpan.FromMinutes(1));
        var id = Assert.Single(first.Core.List()).Id;
        Assert.True(await first.Core.RemoveObservationAsync(id, default));
        Assert.NotEqual(old, await a.GetGenerationAsync("url"));
        Assert.Equal(await a.GetGenerationAsync("url"), await b.GetGenerationAsync("url"));
        Assert.False(await first.Core.RemoveObservationAsync(id, default));
    }

    private static HttpCacheEntry Entry(TimeProvider time) => new()
    {
        RequestUri = "https://user-canary:password-canary@example.test/a?token=query-canary",
        Method = "GET",
        StatusCode = HttpStatusCode.OK,
        StoredAt = time.GetUtcNow(),
        Content = "body-canary"u8.ToArray(),
        Headers = new() { ["Cookie"] = ["cookie-canary"], ["Authorization"] = ["credential-canary"] }
    };
}
