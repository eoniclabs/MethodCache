using MethodCache.Extensibility;
using MethodCache.TestSupport;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Redis.IntegrationTests;

[Collection(RedisCollection.Name)]
public class AtomicInvalidationTests(RedisFixture fixture)
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentConnections_PreserveSignedMaximumAndConcurrentStamps(bool concurrent)
    {
        await using var first = CreateProvider();
        await using var second = CreateProvider();
        var firstCache = Assert.IsAssignableFrom<IAtomicInvalidationStore>(first.GetRequiredKeyedService<IDistributedCache>("MethodCache"));
        var secondCache = Assert.IsAssignableFrom<IAtomicInvalidationStore>(second.GetRequiredKeyedService<IDistributedCache>("MethodCache"));
        var key = "stamp:" + Guid.NewGuid().ToString("N");
        if (concurrent) await InvalidationStampContract.AssertConcurrentMaximumAsync(firstCache, secondCache, key);
        else await InvalidationStampContract.AssertMaximumAsync(firstCache, secondCache, key);
    }

    [SkippableFact]
    public async Task OlderStampWithShortRetention_DoesNotShortenNewerRetention()
    {
        await using var provider = CreateProvider();
        var cache = Assert.IsAssignableFrom<IAtomicInvalidationStore>(provider.GetRequiredKeyedService<IDistributedCache>("MethodCache"));
        var db = (await provider.GetRequiredService<RedisConnection>().GetAsync()).GetDatabase();
        var key = "stamp:" + Guid.NewGuid().ToString("N");
        try
        {
            await cache.WriteInvalidationStampAsync(key, 20, TimeSpan.FromDays(1));
            await cache.WriteInvalidationStampAsync(key, 10, TimeSpan.FromMinutes(1));
            Assert.Equal(20, InvalidationStampContract.Read(Assert.IsType<byte[]>(await cache.GetAsync(key))));
            Assert.True(await db.KeyTimeToLiveAsync(key) > TimeSpan.FromHours(23));
            // Make the backend expire the key immediately, without waiting for wall-clock time in the test.
            await db.KeyExpireAsync(key, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            await cache.WriteInvalidationStampAsync(key, 5, TimeSpan.FromMinutes(1));
            Assert.Equal(5, InvalidationStampContract.Read(Assert.IsType<byte[]>(await cache.GetAsync(key))));
        }
        finally { await cache.RemoveAsync(key); }
    }

    private ServiceProvider CreateProvider() => new ServiceCollection().AddMethodCache(mc => mc.UseRedis(options =>
    {
        options.Configuration = fixture.RequireConfiguration();
        options.EnableBackplane = false;
    })).BuildServiceProvider();
}
