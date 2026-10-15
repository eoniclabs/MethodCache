using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MethodCache.HttpCaching.Internal;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MethodCache.HttpCaching.Tests.Storage;

/// <summary>
/// With the native L2 transport, entries are read back from Redis or SQL Server.
/// Whatever bytes are stored there, reading them must answer a miss or an entry: never throw out of the store and
/// never allocate more than the stored data could hold. (Someone who can write the distributed cache can still
/// store a well-formed response of their choosing; see docs/security.md.)
/// </summary>
public class UntrustedEntryTests
{
    private const int FormatVersion = 2;

    [Fact]
    public void EveryTruncationOfAValidEntry_IsAMiss()
    {
        var data = HttpCacheEntrySerializer.Serialize(Entry(), DateTimeOffset.UtcNow.AddMinutes(1));

        for (var length = 0; length < data.Length; length++)
        {
            Assert.Null(HttpCacheEntrySerializer.Deserialize(data[..length]));
        }
    }

    [Fact]
    public void RandomlyCorruptedEntries_NeverThrow()
    {
        var data = HttpCacheEntrySerializer.Serialize(Entry(), DateTimeOffset.UtcNow.AddMinutes(1));
        var random = new Random(4711);

        for (var i = 0; i < 5_000; i++)
        {
            var corrupt = (byte[])data.Clone();
            for (var flips = random.Next(1, 4); flips > 0; flips--)
            {
                corrupt[random.Next(1, corrupt.Length)] = (byte)random.Next(256);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            HttpCacheEntrySerializer.Deserialize(corrupt); // a miss or some entry; must not throw
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024, $"case {i} allocated too much");
        }
    }

    public static TheoryData<string, byte[]> HugeLengths => new()
    {
        { "body length", Crafted(w => w.Write(256 * 1024 * 1024)) },
        { "negative body length", Crafted(w => w.Write(-1)) },
        { "header count", Crafted(w => { w.Write(0); w.Write(50_000_000); }) },
        { "header value count", Crafted(w => { w.Write(0); w.Write(1); w.Write("X-A"); w.Write(50_000_000); }) },
        { "negative header count", Crafted(w => { w.Write(0); w.Write(-5); }) },
    };

    [Theory]
    [MemberData(nameof(HugeLengths))]
    public void ALengthLargerThanTheData_IsAMiss_WithoutAllocatingIt(string what, byte[] data)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();

        var result = HttpCacheEntrySerializer.Deserialize(data);

        Assert.True(result is null, what);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024, $"{what}: allocated {GC.GetAllocatedBytesForCurrentThread() - before} bytes");
    }

    [Fact]
    public async Task CraftedEntryInTheDistributedCache_IsAMiss()
    {
        var distributed = new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        using var store = new TestHttpStore(distributed: distributed);
        await distributed.SetAsync("methodcache:http:e2:k", Crafted(w => w.Write(256 * 1024 * 1024)));

        // MemoryDistributedCache answers synchronously, so the read runs on this thread
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(await store.GetAsync("k"));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
    }

    /// <summary>A well-formed start of an entry (version, expiry, URI, method, status), then <paramref name="rest"/>.</summary>
    private static byte[] Crafted(Action<BinaryWriter> rest)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)FormatVersion);
            writer.Write(DateTimeOffset.UtcNow.AddHours(1).UtcTicks);
            writer.Write("https://example.com/");
            writer.Write("GET");
            writer.Write(200);
            rest(writer);
        }

        return stream.ToArray();
    }

    private static HttpCacheEntry Entry() => new()
    {
        RequestUri = "https://example.com/a",
        Method = "GET",
        StatusCode = HttpStatusCode.OK,
        Content = Encoding.UTF8.GetBytes("hello"),
        Headers = new Dictionary<string, string[]> { ["ETag"] = new[] { "\"v1\"" }, ["X-A"] = new[] { "1", "2" } },
        ContentHeaders = new Dictionary<string, string[]> { ["Content-Type"] = new[] { "text/plain" } },
        ETag = "\"v1\"",
        LastModified = DateTimeOffset.UtcNow.AddDays(-1),
        CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) },
        Expires = DateTimeOffset.UtcNow.AddMinutes(5),
        Date = DateTimeOffset.UtcNow,
        StoredAt = DateTimeOffset.UtcNow,
        VaryHeaders = new[] { "Accept" },
        VariantGeneration = "g1",
    };
}
