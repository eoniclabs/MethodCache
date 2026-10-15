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
}
