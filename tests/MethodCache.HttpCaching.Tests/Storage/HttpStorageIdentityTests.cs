using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using MethodCache.Core.Runtime.Http;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.HttpCaching.Tests.Storage;

public class HttpStorageIdentityTests
{
    [ThreadStatic] private static string? _sink;

    public static IEnumerable<object[]> Keys()
    {
        yield return [string.Empty];
        yield return [new string('k', 160)];
        yield return ["url:\0\ud800:\udfff:é"];
        yield return [new string('x', 16_385) + "\ud800"];
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task Identity_PreservesFrozenUtf16Digest_WithoutTemporaryCopiesOnLittleEndian(string key)
    {
        var encoded = new byte[checked(key.Length * 2)];
        for (var i = 0; i < key.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(i * 2), key[i]);
        var expected = "http:" + Convert.ToHexString(SHA256.HashData(encoded));
        Assert.Equal(expected, HttpResponseStore.Identity(key));

        var (outputAllocations, actuals) = await MeasureIdentityOnOwnedThread(key);
        if (BitConverter.IsLittleEndian) Assert.Equal(outputAllocations.Min(), actuals.Min());
    }

    [Theory]
    [InlineData("client:abc:m:GET")]
    [InlineData("client:abc|generation:v:m:GET")]
    [InlineData("client:abc:m:GET|generation:v")]
    public async Task BodyBudget_DoesNotMaterializeTemporaryTargetStrings(string key)
    {
        var time = new FakeTimeProvider();
        using var owner = new TestHttpStore(time);
        var storage = owner.ForClient("budget", new HttpCacheOptions());
        var entry = new HttpCacheEntry { RequestUri = "https://example.test/a", Method = "GET", StoredAt = time.GetUtcNow() };
        var (budget, sum, allocated) = await MeasureBodyBudgetOnOwnedThread(storage, key, entry);
        Assert.Equal(budget * 1000, sum);
        Assert.Equal(0, allocated);
        Assert.True(await storage.TrySetAsync(key, entry, TimeSpan.FromMinutes(1), default));
        Assert.Equal("client:abc", Assert.Single(owner.Core.List()).UrlTarget);
    }

    // Preserve the original 100 warmups, one 1,000-call batch and exact-zero assertion.
    // Thread setup/completion and the async test runner stay outside the synchronous probe.
    private static Task<(long Budget, long Sum, long Allocated)> MeasureBodyBudgetOnOwnedThread(
        IHttpCacheStorage storage, string key, HttpCacheEntry entry)
    {
        var completion = new TaskCompletionSource<(long, long, long)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ExecutionContext.SuppressFlow())
        {
            new Thread(() =>
            {
                try
                {
                    var budget = storage.BodyBudget(key, entry);
                    for (var i = 0; i < 100; i++) _ = storage.BodyBudget(key, entry);
                    var (sum, allocated) = MeasureBodyBudget(storage, key, entry);
                    completion.SetResult((budget, sum, allocated));
                }
                catch (Exception error) { completion.SetException(error); }
            })
            { IsBackground = true }.Start();
        }
        return completion.Task;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Sum, long Allocated) MeasureBodyBudget(IHttpCacheStorage storage, string key, HttpCacheEntry entry)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        long sum = 0;
        for (var i = 0; i < 1000; i++) sum += storage.BodyBudget(key, entry);
        return (sum, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public async Task Replacement_RefreshesEntryMetadata_AndUsesCurrentClientAndTags(bool changeClient, bool changeTag, bool selector)
    {
        var time = new FakeTimeProvider();
        using var owner = new TestHttpStore(time);
        const string key = "client:abc:m:GET";
        var firstOptions = new HttpCacheOptions();
        firstOptions.Tags.Add("old");
        var first = owner.ForClient("first", firstOptions);
        var original = new HttpCacheEntry { RequestUri = "https://example.test/a", Method = "GET", StoredAt = time.GetUtcNow(), Content = [1] };
        Assert.True(await first.TrySetAsync(key, original, TimeSpan.FromSeconds(5), default));
        var prior = Assert.IsType<HttpStorageInfo>(owner.Core.FindLocalInfo(key));

        time.Advance(TimeSpan.FromSeconds(1));
        var client = changeClient ? "second" : "first";
        var tag = changeTag ? "new" : "old";
        var options = new HttpCacheOptions();
        options.Tags.Add(tag);
        var replacementView = owner.ForClient(client, options);
        var replacement = original with { StoredAt = time.GetUtcNow(), Content = new byte[2048], VariantGeneration = selector ? "variant" : null };
        Assert.True(await replacementView.TrySetAsync(key, replacement, TimeSpan.FromSeconds(10), default));

        var current = Assert.IsType<HttpStorageInfo>(owner.Core.FindLocalInfo(key));
        Assert.Equal(prior.Id, current.Id);
        Assert.Equal(prior.UrlTarget, current.UrlTarget);
        Assert.Equal(client, current.Client);
        Assert.Equal(new[] { tag, "http:" + MethodCacheKeys.Part(client) }.Order(StringComparer.Ordinal), current.Tags);
        Assert.Equal(replacement.StoredAt, current.CreatedAt);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromSeconds(10), current.Deadline);
        Assert.Equal(selector, current.Selector);
        Assert.Equal(owner.Core.MaximumBytes - replacementView.BodyBudget(key, replacement) - 256, current.Cost);
        Assert.True(current.Cost > prior.Cost);
        Assert.Same(replacement, await replacementView.GetAsync(key));

        if (changeTag || changeClient)
        {
            var retiredTag = changeTag ? "old" : "http:" + MethodCacheKeys.Part("first");
            await owner.Core.InvalidateTagsAsync([retiredTag], default);
            Assert.Same(replacement, await replacementView.GetAsync(key));
        }
        await owner.Core.InvalidateTagsAsync([tag], default);
        Assert.Null(await replacementView.GetAsync(key));
    }

    // Keep the exact probe away from the test runner's worker and ambient execution context.
    // Creating the thread/delegates, hashing warmup, completion and assertions are outside Measure.
    private static Task<(long[] OutputAllocations, long[] Actuals)> MeasureIdentityOnOwnedThread(string key)
    {
        var completion = new TaskCompletionSource<(long[], long[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ExecutionContext.SuppressFlow())
        {
            new Thread(() =>
            {
                try
                {
                    Func<string> identity = () => HttpResponseStore.Identity(key);
                    Func<string> outputOnly = static () => new string('x', 69);
                    _ = Measure(identity);
                    _ = Measure(outputOnly);
                    // Use the minimum of three fixed batches for each path, with no byte tolerance.
                    // A transient first-use allocation in the reference is not an identity allocation;
                    // sustained per-call temporary copies remain present in every measured batch.
                    var outputAllocations = new long[3];
                    var actuals = new long[3];
                    for (var i = 0; i < 3; i++)
                    {
                        outputAllocations[i] = Measure(outputOnly);
                        actuals[i] = Measure(identity);
                    }
                    completion.SetResult((outputAllocations, actuals));
                }
                catch (Exception error) { completion.SetException(error); }
                finally { _sink = null; }
            })
            { IsBackground = true }.Start();
        }
        return completion.Task;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(Func<string> create)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) _sink = create(); // both results escape; no unused allocation is optimized away
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
