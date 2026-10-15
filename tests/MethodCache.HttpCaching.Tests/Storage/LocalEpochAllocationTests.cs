using Xunit;

namespace MethodCache.HttpCaching.Tests.Storage;

public class LocalEpochAllocationTests
{
    [Fact]
    public async Task UnchangedLocalEpochVector_DoesNotAllocatePerLookup()
    {
        using var storage = new TestHttpStore();
        string[] tags = ["http:test", "catalog"];
        for (var i = 0; i < 1000; i++) _ = await storage.Core.CaptureAsync("url", tags, default);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++) _ = await storage.Core.CaptureAsync("url", tags, default);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
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
