using MethodCache.HttpCaching.Internal;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class HttpSelectingHeaderAllocationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NoConfiguredOrVaryHeaders_AllocatesOnlyTheSelectingArray(bool shared, bool emptyVary)
    {
        var options = new HttpCacheOptions { IsSharedCache = shared };
        string[]? vary = emptyVary ? [] : null;
        const int count = 10000;
        for (var i = 0; i < count; i++) _ = HttpCacheKeys.SelectingHeaders(options, vary);
        var before = GC.GetAllocatedBytesForCurrentThread();
        string[] headers = [];
        for (var i = 0; i < count; i++) headers = HttpCacheKeys.SelectingHeaders(options, vary);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(shared ? [] : new[] { "Authorization", "Cookie" }, headers);
        // One fresh two-element array for private caches, or the empty array for shared caches.
        // Eight bytes per call allow incidental runtime bookkeeping, but no set or sorting pipeline.
        Assert.True(allocated <= count * (shared ? 8L : 48L), $"Allocated {allocated / (double)count} B per selection.");
        if (!shared)
        {
            headers[0] = "caller mutation";
            Assert.Equal("Authorization", HttpCacheKeys.SelectingHeaders(options, vary)[0]);
        }
        options.KeyHeaders.Add("X-Tenant");
        Assert.Contains("X-Tenant", HttpCacheKeys.SelectingHeaders(options, vary));
        Assert.Contains("Accept", HttpCacheKeys.SelectingHeaders(options, ["Accept"]));
    }
}
