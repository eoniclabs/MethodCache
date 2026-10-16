using MethodCache.Infrastructure;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace MethodCache.Redis.IntegrationTests;

[Collection(RedisCollection.Name)]
public class EpochContractTests(RedisFixture fixture)
{
    [SkippableFact]
    public async Task NativeAtomicEpochs_ConformAcrossIndependentConnections()
    {
        var configuration = fixture.RequireConfiguration();
        using var first = new RedisConnection(new RedisOptions { Configuration = configuration }, new ServiceCollection().BuildServiceProvider());
        using var second = new RedisConnection(new RedisOptions { Configuration = configuration }, new ServiceCollection().BuildServiceProvider());
        var firstConnection = await first.GetAsync();
        var secondConnection = await second.GetAsync();
        await Task.WhenAll(firstConnection.GetDatabase().PingAsync(), secondConnection.GetDatabase().PingAsync());
        var one = new RedisDistributedCache(first);
        var two = new RedisDistributedCache(second);
        var prefix = "epoch:" + Guid.NewGuid().ToString("N") + ":";
        await MethodCache.TestSupport.EpochBackendContract.RunAsync(one, two, prefix);
        var ttl = await firstConnection.GetDatabase().KeyTimeToLiveAsync(RedisDistributedCache.GetPhysicalKey(prefix + "tag"));
        Assert.Null(ttl); // Ensure/Advance HTTP markers are permanent.
    }

    [SkippableFact]
    public async Task EnsureEpochs_InitializedVector_DoesNotWrite()
    {
        var configuration = fixture.RequireConfiguration();
        using var connection = new RedisConnection(new RedisOptions { Configuration = configuration }, new ServiceCollection().BuildServiceProvider());
        var cache = new RedisDistributedCache(connection);
        var prefix = "epoch-read:" + Guid.NewGuid().ToString("N") + ":";
        string[] keys = [prefix + "a", prefix + "b"];
        await using var writes = await KeyWrites.WatchAsync(configuration, keys.Select(key => (RedisKey)RedisDistributedCache.GetPhysicalKey(key)));
        try
        {
            var first = await cache.EnsureEpochsAsync(keys);
            Assert.Contains("hset", await writes.CollectAsync()); // the watch sees initialization

            Assert.Equal(first, await cache.EnsureEpochsAsync(keys));
            Assert.Empty(await writes.CollectAsync());
        }
        finally { foreach (var key in keys) await cache.RemoveAsync(key); }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureEpochs_ExpiringEpoch_BecomesPermanentBeforeReadOnlyReuse(bool sliding)
    {
        var configuration = fixture.RequireConfiguration();
        using var connection = new RedisConnection(new RedisOptions { Configuration = configuration }, new ServiceCollection().BuildServiceProvider());
        var cache = new RedisDistributedCache(connection);
        var key = "epoch-retention:" + Guid.NewGuid().ToString("N");
        var physical = (RedisKey)RedisDistributedCache.GetPhysicalKey(key);
        var db = (await connection.GetAsync()).GetDatabase();
        try
        {
            // An initialized epoch that an ordinary entry write left with an expiry
            await cache.SetAsync(key, EpochMetadata.Ensure(null, Guid.NewGuid()), sliding
                ? new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(1) }
                : new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) });
            Assert.NotNull(await db.KeyTimeToLiveAsync(physical));

            var first = (await cache.EnsureEpochsAsync([key]))[0];
            Assert.Null(await db.KeyTimeToLiveAsync(physical));
            Assert.Equal("-1", (string?)await db.HashGetAsync(physical, "absexp"));
            Assert.Equal("-1", (string?)await db.HashGetAsync(physical, "sldexp"));

            await using var writes = await KeyWrites.WatchAsync(configuration, [physical]);
            Assert.Equal(first, (await cache.EnsureEpochsAsync([key]))[0]);
            Assert.Empty(await writes.CollectAsync());
        }
        finally { await cache.RemoveAsync(key); }
    }
}
