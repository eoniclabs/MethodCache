using MethodCache.TestSupport;
using MethodCache.Extensibility;
using Microsoft.Extensions.Caching.Distributed;
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
        // Keep physical native TTLs ahead of wall-clock setup; expiry assertions use source deadlines on this clock.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(10));
        var prefix = "policy:" + Guid.NewGuid().ToString("N") + ":";
        var configuration = redis.RequireConfiguration();
        await PolicyBackendContract.RunAsync((origin, configure) => NodeAsync(time, prefix, configuration, origin, configure), time);
    }

    [SkippableFact]
    public async Task ExpirationRules_ConformToSharedContract()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(10));
        var prefix = "expiration:" + Guid.NewGuid().ToString("N") + ":";
        var configuration = redis.RequireConfiguration();
        await ExpirationContract.RunAsync((origin, configure) => NodeAsync(time, prefix, configuration, origin, configure), time);
    }

    private static async Task<ServiceProvider> NodeAsync(FakeTimeProvider time, string prefix, string configuration,
        PolicyBackendContract.Origin origin, Action<MethodCacheBuilder>? configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<PolicyBackendContract.IOrigin>(origin);
        services.AddMethodCache(b => { b.FromAssemblyOf<PolicyBackendContract.Origin>(); configure?.Invoke(b); });
        services.Configure<DistributedCacheOptions>(o => { o.KeyPrefix = prefix; o.ClockSkewTolerance = TimeSpan.Zero; o.TagCheckInterval = TimeSpan.Zero; });
        services.AddMethodCache(mc => mc.UseRedis(o => { o.Configuration = configuration; }));
        PolicySubscriptionBarrier.Decorate(services);
        var provider = services.BuildServiceProvider();
        try
        {
            var connection = await provider.GetRequiredService<RedisConnection>().GetAsync();
            await RedisFixture.WaitForSubscriberAsync(connection);
            await PolicySubscriptionBarrier.WaitForNodeAsync(provider);
            return provider;
        }
        catch { await provider.DisposeAsync(); throw; }
    }

    [SkippableTheory]
    [InlineData("partial")]
    [InlineData("cancellation")]
    [InlineData("validation")]
    public async Task NativeInvalidationFaults_ConformToSharedContract(string scenario)
    {
        var services = new ServiceCollection();
        services.Configure<DistributedCacheOptions>(o => o.KeyPrefix = "policy-fault:" + Guid.NewGuid().ToString("N") + ":");
        services.AddMethodCache(mc => mc.UseRedis(redis.RequireConfiguration()));
        await using var provider = services.BuildServiceProvider();
        // A pub/sub connection that comes up after the backplane subscribed counts as a reconnect, which clears memory
        // the contract expects to keep
        await RedisFixture.WaitForSubscriberAsync(await provider.GetRequiredService<RedisConnection>().GetAsync());
        var store = Assert.IsAssignableFrom<IAtomicInvalidationStore>(provider.GetRequiredKeyedService<IDistributedCache>("MethodCache"));
        await NativePolicyFaultContract.RunAsync(scenario, store, provider.GetRequiredKeyedService<IBackplane>("MethodCache"));
    }

}
