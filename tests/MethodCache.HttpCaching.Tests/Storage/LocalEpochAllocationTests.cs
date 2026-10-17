using Xunit;
using MethodCache.HttpCaching.Internal;
using MethodCache.Core.Runtime.Http;
using System.Runtime.CompilerServices;

namespace MethodCache.HttpCaching.Tests.Storage;

public class LocalEpochAllocationTests
{
    [Fact]
    public void InitialLocalNamespaces_DoNotMaterializeUnusedEpochVectors()
    {
        var perSlot = MeasureInitialNamespaces();
        Assert.True(perSlot < 192, $"Initial slot metadata costs {perSlot:F1} B; unused epoch vectors must stay lazy.");
    }

    private static double MeasureInitialNamespaces()
    {
        using var storage = new TestHttpStore();
        string[] tags = ["http:test"];
        // Warm first-use runtime paths separately, then capture every remaining fresh bounded slot.
        for (var slot = 0; slot < 64; slot++)
            _ = storage.Core.CaptureSlotAsync(slot, tags, default, allowInitialNamespace: true).Result;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var slot = 64; slot < HttpResponseStore.GenerationSlots; slot++)
        {
            var captured = storage.Core.CaptureSlotAsync(slot, tags, default, allowInitialNamespace: true);
            if (!captured.IsCompletedSuccessfully || captured.Result != string.Empty)
                throw new InvalidOperationException("Initial default namespaces must stay empty and synchronous.");
        }
        return (GC.GetAllocatedBytesForCurrentThread() - before) / (double)(HttpResponseStore.GenerationSlots - 64);
    }

    [Fact]
    public async Task ConcurrentFirstCaptures_KeepOneIdentity_AndObserveLaterInvalidation()
    {
        using var storage = new TestHttpStore();
        string[] tags = ["client", "catalog"];
        static Task<string?>[] Readers(HttpResponseStore store, string[] tags, Task start)
            => Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
            {
                await start;
                return await store.CaptureAsync("url", tags, default);
            })).ToArray();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readers = Readers(storage.Core, tags, start.Task);
        start.SetResult();
        var identities = await Task.WhenAll(readers);
        var first = Assert.Single(identities.Distinct());
        Assert.False(string.IsNullOrEmpty(first));
        storage.Core.InvalidateLocalTag("catalog");
        var later = await Task.WhenAll(Readers(storage.Core, tags, Task.CompletedTask));
        Assert.NotEqual(first, Assert.Single(later.Distinct()));
        using var independent = new TestHttpStore();
        Assert.NotEqual(first, await independent.Core.CaptureAsync("url", tags, default));
    }

    [Fact]
    public async Task UnchangedLocalEpochVector_DoesNotAllocatePerLookup()
    {
        var allocated = await MeasureCapturesOnOwnedThread();
        Assert.Contains(0L, allocated);
    }

    // Keep the exact synchronous allocation probe off the runner's worker and ambient context.
    // Store creation, the original 1,000 warmups, completion and assertions are outside MeasureCaptures.
    // Complete three fixed batches and require an exact zero-byte steady-state batch. This detects
    // sustained per-call allocations without attributing every transient runtime allocation to capture.
    // Every capture also checks identity reuse, so even a single returned identity copy is rejected.
    private static Task<long[]> MeasureCapturesOnOwnedThread()
    {
        var completion = new TaskCompletionSource<long[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ExecutionContext.SuppressFlow())
        {
            new Thread(() =>
            {
                try
                {
                    using var storage = new TestHttpStore();
                    string[] tags = ["http:test", "catalog"];
                    string? expected = null;
                    for (var i = 0; i < 1000; i++)
                    {
                        var warmup = storage.Core.CaptureAsync("url", tags, default);
                        if (!warmup.IsCompletedSuccessfully) throw new InvalidOperationException("Local epoch capture must complete synchronously.");
                        expected = warmup.Result;
                    }
                    if (expected == null) throw new InvalidOperationException("Local epoch capture must return an identity.");
                    var allocated = new long[3];
                    for (var batch = 0; batch < allocated.Length; batch++)
                        allocated[batch] = MeasureCaptures(storage.Core, tags, expected);
                    completion.SetResult(allocated);
                }
                catch (Exception error) { completion.SetException(error); }
            })
            { IsBackground = true }.Start();
        }
        return completion.Task;
    }

    // Keep assertion initialization and the async test state machine outside the measured region.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureCaptures(HttpResponseStore storage, string[] tags, string? expected)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            var captured = storage.CaptureAsync("url", tags, default);
            if (!captured.IsCompletedSuccessfully) throw new InvalidOperationException("Local epoch capture must complete synchronously.");
            if (!ReferenceEquals(expected, captured.Result))
                throw new InvalidOperationException("Unchanged local epochs must reuse the captured identity object.");
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public async Task DefaultLocalClient_InitialNamespaceIsEmpty_AndNeverReturnsAfterInvalidation()
    {
        using var storage = new TestHttpStore();
        var key = HttpCacheKeys.ForUri("test", HttpMethod.Get, new Uri("https://example.test/a"));
        Assert.Empty(await storage.GetGenerationAsync(key));
        await storage.InvalidateAsync(key);
        var afterUrl = await storage.GetGenerationAsync(key);
        Assert.NotEmpty(afterUrl);
        storage.Core.InvalidateLocalTag("http:test");
        var afterTag = await storage.GetGenerationAsync(key);
        Assert.NotEmpty(afterTag); Assert.NotEqual(afterUrl, afterTag);
        await storage.ClearAsync();
        var afterClear = await storage.GetGenerationAsync(key);
        Assert.NotEmpty(afterClear); Assert.NotEqual(afterTag, afterClear);
    }

    [Fact]
    public async Task MemoizedVector_StillObservesUrlTagAndClearChanges_WithoutChangingOtherTagSets()
    {
        using var storage = new TestHttpStore();
        string[] a = ["client-a", "catalog"], b = ["client-b", "other"];
        var a1 = await storage.Core.CaptureAsync("url", a, default);
        var b1 = await storage.Core.CaptureAsync("url", b, default);
        Assert.Equal(a1, await storage.Core.CaptureAsync("url", ["client-a", "catalog"], default));
        Assert.Equal(b1, await storage.Core.CaptureAsync("url", b, default));
        storage.Core.InvalidateLocalTag("catalog");
        Assert.NotEqual(a1, await storage.Core.CaptureAsync("url", a, default));
        Assert.Equal(b1, await storage.Core.CaptureAsync("url", b, default));
        var a2 = await storage.Core.CaptureAsync("url", a, default);
        await storage.Core.InvalidateUrlsAsync(["url"], default);
        Assert.NotEqual(a2, await storage.Core.CaptureAsync("url", a, default));
        var a3 = await storage.Core.CaptureAsync("url", a, default);
        storage.Core.ClearLocal();
        Assert.NotEqual(a3, await storage.Core.CaptureAsync("url", a, default));
    }
}
