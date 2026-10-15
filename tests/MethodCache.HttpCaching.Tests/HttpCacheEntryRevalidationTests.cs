using System.Net;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class HttpCacheEntryRevalidationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpdatedHeaders_MergesWithoutMutatingTheStoredRepresentation(bool connectionOptions)
    {
        var stored = new HttpCacheEntry
        {
            RequestUri = "https://benchmark.invalid/revalidated",
            Method = "GET",
            Headers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Retained"] = ["original"],
                ["X-Replaced"] = ["old"]
            },
            ContentHeaders = new(StringComparer.OrdinalIgnoreCase) { ["Content-Length"] = ["4"] },
            Content = [1, 2, 3, 4]
        };
        using var notModified = new HttpResponseMessage(HttpStatusCode.NotModified)
        {
            Content = new ByteArrayContent([])
        };
        notModified.Headers.TryAddWithoutValidation("x-replaced", ["new", "second"]);
        notModified.Headers.TryAddWithoutValidation("Set-Cookie", "private=value");
        notModified.Headers.TryAddWithoutValidation("Keep-Alive", "timeout=5");
        notModified.Headers.TryAddWithoutValidation("Age", "30");
        notModified.Headers.TryAddWithoutValidation("X-Hop", "hop-value");
        if (connectionOptions) notModified.Headers.Connection.Add("x-hop");
        notModified.Content.Headers.ContentLength = 0;

        var updated = stored.WithUpdatedHeaders(notModified, DateTimeOffset.UnixEpoch);

        Assert.Equal(["original"], updated.Headers["X-Retained"]);
        Assert.Equal(["new", "second"], updated.Headers["X-Replaced"]);
        Assert.Equal(["old"], stored.Headers["X-Replaced"]);
        Assert.Equal(["4"], updated.ContentHeaders["Content-Length"]);
        Assert.Equal(connectionOptions, !updated.Headers.ContainsKey("X-Hop"));
        Assert.False(updated.Headers.ContainsKey("Connection"));
        Assert.False(updated.Headers.ContainsKey("Set-Cookie"));
        Assert.False(updated.Headers.ContainsKey("Keep-Alive"));
        Assert.False(updated.Headers.ContainsKey("Age"));
        Assert.Same(stored.Content, updated.Content);
        Assert.NotSame(stored.Headers, updated.Headers);
        Assert.NotSame(stored.ContentHeaders, updated.ContentHeaders);
    }
}
