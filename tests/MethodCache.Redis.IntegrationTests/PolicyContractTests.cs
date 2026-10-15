using MethodCache.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;
namespace MethodCache.Redis.IntegrationTests;

[Collection(RedisCollection.Name)]
public class PolicyContractTests(RedisFixture redis)
{
    [SkippableFact]
    public async Task PolicyTransitionsAndDeadlines_ConformToSharedContract()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(10));
        var prefix = "policy:" + Guid.NewGuid().ToString("N") + ":";
        var configuration = redis.RequireConfiguration();
        async Task<ServiceProvider> Create(PolicyBackendContract.Origin origin, Action<MethodCacheBuilder>? configure)
        {
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(time);
            services.AddSingleton<PolicyBackendContract.IOrigin>(origin);
            services.AddMethodCache(b => { b.FromAssemblyOf<PolicyBackendContract.Origin>(); configure?.Invoke(b); });
            services.Configure<DistributedCacheOptions>(o => { o.KeyPrefix = prefix; o.ClockSkewTolerance = TimeSpan.Zero; o.TagCheckInterval = TimeSpan.Zero; });
            services.AddMethodCacheRedis(o => { o.Configuration = configuration; });
            PolicySubscriptionBarrier.Decorate(services);
            var provider = services.BuildServiceProvider();
            try
            {
                var connection = await provider.GetRequiredService<RedisConnection>().GetAsync();
                await connection.GetSubscriber().PingAsync();
                await PolicySubscriptionBarrier.WaitForNodeAsync(provider);
                return provider;
            }
            catch { await provider.DisposeAsync(); throw; }
        }
        await PolicyBackendContract.RunAsync(Create, time);
    }
}
