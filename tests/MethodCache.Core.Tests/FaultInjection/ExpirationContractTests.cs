using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using MethodCache.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.FaultInjection;

/// <summary>The expiration contract over two instances, on a distributed cache held in memory.</summary>
public class ExpirationContractTests
{
    [Fact]
    public async Task ExpirationRules_ConformToSharedContract()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(10));
        var store = new AtomicTestCache(time);
        var hub = new ChaosBackplaneHub(time);
        Task<ServiceProvider> Create(PolicyBackendContract.Origin origin, Action<MethodCacheBuilder>? configure)
        {
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(time);
            services.AddSingleton<PolicyBackendContract.IOrigin>(origin);
            services.AddKeyedSingleton<IBackplane>(OwnedServices.Key, hub.Join());
            services.AddMethodCache(b => { b.FromAssemblyOf<PolicyBackendContract.Origin>(); configure?.Invoke(b); });
            services.AddMethodCacheDistributedCache(_ => store, o => { o.ClockSkewTolerance = TimeSpan.Zero; o.TagCheckInterval = TimeSpan.Zero; });
            var provider = services.BuildServiceProvider();
            provider.GetRequiredService<ICacheManager>(); // subscribes to the backplane, which the contract then leaves
            return Task.FromResult(provider);
        }
        await ExpirationContract.RunAsync(Create, time);
    }
}
