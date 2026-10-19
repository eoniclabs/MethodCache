using MethodCache.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Redis.IntegrationTests;

[Collection(RedisCollection.Name)]
public class HttpContractTests(RedisFixture redis)
{
    [SkippableFact]
    public async Task HttpEpochs_WithDroppedBackplane_NoOpAndRollingFormats_ConformToContract()
    {
        var configuration = redis.RequireConfiguration(); var prefix = "http-contract:" + Guid.NewGuid().ToString("N") + ":";
        async Task<ServiceProvider> Create(HttpNativeContract.Origin origin, byte format, bool http)
        {
            var services = new ServiceCollection();
            services.AddMethodCache(mc => mc.UseRedis(o => o.Configuration = configuration));
            services.Configure<DistributedCacheOptions>(o => { o.KeyPrefix = prefix; o.ClockSkewTolerance = TimeSpan.Zero; o.OperationTimeout = TimeSpan.FromSeconds(5); });
            HttpNativeContract.Configure(services, origin, format, http);
            var provider = services.BuildServiceProvider();
            var connection = await provider.GetRequiredService<RedisConnection>().GetAsync();
            await connection.GetDatabase().PingAsync();
            return provider;
        }
        await HttpNativeContract.RunAsync(Create);
    }
}
