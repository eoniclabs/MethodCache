using MethodCache.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Redis.IntegrationTests;

[Collection(RedisCollection.Name)]
public class PolicyStateAcceptanceTests(RedisFixture redis)
{
    [SkippableTheory]
    [InlineData("lookup")]
    [InlineData("equivalent")]
    [InlineData("reload")]
    [InlineData("specificity")]
    [InlineData("disabled")]
    [InlineData("refresh")]
    [InlineData("custom")]
    [InlineData("sync")]
    [InlineData("unready-sync")]
    [InlineData("lost-message")]
    public async Task PolicyState_ConformsToSharedNativeContract(string scenario)
    {
        // Keep physical native TTLs ahead of wall-clock setup; expiry assertions use source deadlines on this clock.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(10));
        var prefix = "policy-state:" + Guid.NewGuid().ToString("N") + ":";
        var configuration = redis.RequireConfiguration();
        async Task<ServiceProvider> Create(PolicyBackendContract.Origin origin, Action<MethodCacheBuilder>? configure)
        {
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(time);
            services.AddSingleton<PolicyBackendContract.IOrigin>(origin);
            services.AddMethodCache(b => { b.FromAssemblyOf<PolicyBackendContract.Origin>(); configure?.Invoke(b); });
            services.Configure<DistributedCacheOptions>(o => { o.KeyPrefix = prefix; o.ClockSkewTolerance = TimeSpan.Zero; o.TagCheckInterval = TimeSpan.Zero; });
            services.AddMethodCache(mc => mc.UseRedis(configuration));
            if (scenario is "unready-sync" or "disabled" or "refresh") GatedNativePolicyStore.Decorate(services, holdReads: scenario == "unready-sync");
            PolicyMessageGate.Decorate(services);
            var provider = services.BuildServiceProvider();
            try
            {
                await RedisFixture.WaitForSubscriberAsync(await provider.GetRequiredService<RedisConnection>().GetAsync());
                if (scenario != "custom")
                {
                    // A connected pub/sub socket alone does not acknowledge the manager's subscription.
                    // Finish initial backplane setup before any factory/publication assertions begin.
                    _ = provider.GetRequiredService<ICacheManager>();
                    await provider.GetRequiredService<PolicyMessageGate>().Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                }
                return provider;
            }
            catch { await provider.DisposeAsync(); throw; }
        }
        await PolicyBackendContract.RunAcceptanceAsync(scenario, Create, time);
    }
}
