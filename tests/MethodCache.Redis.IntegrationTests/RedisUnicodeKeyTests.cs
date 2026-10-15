using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;
using Xunit;

namespace MethodCache.Redis.IntegrationTests;

[Collection(RedisCollection.Name)]
public sealed class RedisUnicodeKeyTests(RedisFixture fixture)
{
    [SkippableFact]
    public async Task AllCacheOperationsAndAtomicStamps_KeepUtf16KeysDistinct()
    {
        using var multiplexer = await ConnectionMultiplexer.ConnectAsync(fixture.RequireConfiguration());
        using var connection = new RedisConnection(() => Task.FromResult<IConnectionMultiplexer>(multiplexer), owned: false);
        var cache = new RedisDistributedCache(connection);
        var prefix = "unicode:" + Guid.NewGuid().ToString("N") + ":";
        var keys = new[] { prefix + "\ud800", prefix + "\ud801", prefix + "\ufffd", prefix + "\\uD800", prefix + "\ud800\udc00", prefix + "\0" };
        var options = new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(1) };
        try
        {
            for (var i = 0; i < keys.Length; i++)
            {
                var bytes = new byte[] { (byte)i };
                switch (i % 4)
                {
                    case 0: cache.Set(keys[i], bytes, options); break;
                    case 1: await cache.SetAsync(keys[i], bytes, options); break;
                    case 2: cache.Set(keys[i], new ReadOnlySequence<byte>(bytes), options); break;
                    default: await cache.SetAsync(keys[i], new ReadOnlySequence<byte>(bytes), options); break;
                }
            }
            for (var i = 0; i < keys.Length; i++)
            {
                Assert.Equal(new byte[] { (byte)i }, cache.Get(keys[i]));
                Assert.Equal(new byte[] { (byte)i }, await cache.GetAsync(keys[i]));
                var buffer = new ArrayBufferWriter<byte>();
                Assert.True(cache.TryGet(keys[i], buffer));
                Assert.Equal(new byte[] { (byte)i }, buffer.WrittenSpan.ToArray());
                buffer.Clear();
                Assert.True(await cache.TryGetAsync(keys[i], buffer));
                Assert.Equal(new byte[] { (byte)i }, buffer.WrittenSpan.ToArray());
                cache.Refresh(keys[i]);
                await cache.RefreshAsync(keys[i]);
            }
            cache.Remove(keys[0]);
            Assert.Null(await cache.GetAsync(keys[0]));
            Assert.Equal(new byte[] { 1 }, await cache.GetAsync(keys[1]));
            await cache.RemoveAsync(keys[1]);
            Assert.Null(cache.Get(keys[1]));
            // Timestamp metadata has its own schema, separate from the preceding arbitrary value bytes.
            foreach (var key in keys) await cache.RemoveAsync(key);
            for (var i = 0; i < keys.Length; i++)
            {
                await cache.WriteInvalidationStampAsync(keys[i], 10 + i, TimeSpan.FromMinutes(1));
                await cache.WriteInvalidationStampAsync(keys[i], 1, TimeSpan.FromMinutes(1));
            }
            for (var i = 0; i < keys.Length; i++)
                Assert.Equal(10 + i, BinaryPrimitives.ReadInt64LittleEndian((await cache.GetAsync(keys[i]))!));
        }
        finally
        {
            // The generated ASCII prefix is unchanged by the encoding. Remove only this test's physical keys,
            // including colliding keys left by the failing pre-fix run.
            var database = multiplexer.GetDatabase();
            foreach (var endpoint in multiplexer.GetEndPoints())
                foreach (var key in multiplexer.GetServer(endpoint).Keys(pattern: prefix + "*"))
                    await database.KeyDeleteAsync(key);
        }
    }
}
