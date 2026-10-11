using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using MethodCache.Core.PolicyPipeline.Resolution;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Core.Tests.PolicyPipeline;

public class PolicyHandleTests
{
    private static readonly string MethodId = $"{typeof(IOrders).FullName!.Replace('+', '.')}.Get";

    [Fact]
    public async Task Handle_FollowsRuntimeOverrides_AsSoonAsTheCallReturns()
    {
        await using var provider = BuildProvider();
        var registry = provider.GetRequiredService<IPolicyRegistry>();
        var handle = PolicyHandle.For(registry, MethodId);
        Assert.Equal(TimeSpan.FromMinutes(5), handle.Current.Duration);

        var configurator = provider.GetRequiredService<IRuntimeCacheConfigurator>();
        await configurator.UpsertAsync<IOrders, string>(o => o.Get(0), b => b.WithDuration(TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(30), handle.Current.Duration);

        await configurator.RemoveAsync<IOrders, string>(o => o.Get(0));
        Assert.Equal(TimeSpan.FromMinutes(5), handle.Current.Duration);
    }

    [Fact]
    public async Task Handle_IsSharedPerMethod()
    {
        await using var provider = BuildProvider();
        var registry = provider.GetRequiredService<IPolicyRegistry>();

        Assert.Same(PolicyHandle.For(registry, MethodId), PolicyHandle.For(registry, MethodId));
    }

    [Fact]
    public async Task CachedMethod_RebuildsKeyPrefix_WhenVersionChanges()
    {
        await using var provider = BuildProvider();
        var registry = provider.GetRequiredService<IPolicyRegistry>();
        var method = new CachedMethod(PolicyHandle.For(registry, MethodId), typeof(IOrders), "Get");
        Assert.Equal(MethodCacheKeys.Prefix(typeof(IOrders), "Get"), method.Current.KeyPrefix);

        var configurator = provider.GetRequiredService<IRuntimeCacheConfigurator>();
        await configurator.UpsertAsync<IOrders, Task<string>>(o => o.Get(0), b => b.WithVersion(2));
        Assert.Equal(2, method.Current.Policy.Version);

        Assert.Equal(MethodCacheKeys.Prefix(typeof(IOrders), "Get", 2), method.Current.KeyPrefix);
        Assert.Equal(MethodCacheKeys.Prefix(typeof(IOrders), "Get"), method.KeyMethod);
    }

    [Fact]
    public async Task GetSites_CreatesOncePerContainer()
    {
        await using var first = BuildProvider();
        await using var second = BuildProvider();
        var firstServices = first.GetRequiredService<DecoratorServices>();
        var secondServices = second.GetRequiredService<DecoratorServices>();

        var sites = firstServices.GetSites(static r => new Sites(r));

        Assert.Same(sites, firstServices.GetSites(static r => new Sites(r)));
        Assert.Same(firstServices, first.GetRequiredService<DecoratorServices>());
        Assert.NotSame(sites, secondServices.GetSites(static r => new Sites(r)));
    }

    [Fact]
    public async Task SourcesWithTheSamePriority_BothContribute()
    {
        // Before, the second source replaced the first, so which one won depended on registration order
        var durationSource = new StaticSource("first", CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(3) }, CachePolicyFields.Duration);
        var tagSource = new StaticSource("second", CachePolicy.Empty with { Tags = new[] { "orders" } }, CachePolicyFields.Tags);
        await using var resolver = new PolicyResolver(new[]
        {
            new PolicySourceRegistration(durationSource, PolicySourcePriority.Attributes),
            new PolicySourceRegistration(tagSource, PolicySourcePriority.Attributes)
        });

        var result = await resolver.ResolveAsync(MethodId);

        Assert.Equal(TimeSpan.FromMinutes(3), result.Policy.Duration);
        Assert.Contains("orders", result.Policy.Tags);
    }

    [Fact]
    public void Container_CanBeDisposedSynchronously()
    {
        var provider = BuildProvider();
        _ = PolicyHandle.For(provider.GetRequiredService<IPolicyRegistry>(), MethodId);

        provider.Dispose();
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddMethodCache(
            config => config.ForService<IOrders>().Method(o => o.Get(default)).Configure(o => o.WithDuration(TimeSpan.FromMinutes(5))),
            Assembly.GetExecutingAssembly());
        return services.BuildServiceProvider();
    }

    public interface IOrders
    {
        Task<string> Get(int id);
    }

    private sealed class Sites
    {
        public Sites(IPolicyRegistry registry) => Get = new CachedMethod(PolicyHandle.For(registry, MethodId), typeof(IOrders), "Get");

        public CachedMethod Get { get; }
    }

    private sealed class StaticSource : IPolicySource
    {
        private readonly PolicySnapshot _snapshot;

        public StaticSource(string sourceId, CachePolicy policy, CachePolicyFields fields)
        {
            SourceId = sourceId;
            var now = DateTimeOffset.UtcNow;
            _snapshot = new PolicySnapshot(sourceId, MethodId, CachePolicyMapper.AttachContribution(policy, sourceId, fields, now), now);
        }

        public string SourceId { get; }

        public Task<IReadOnlyCollection<PolicySnapshot>> GetSnapshotAsync(System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<PolicySnapshot>>(new[] { _snapshot });

        public IAsyncEnumerable<PolicyChange> WatchAsync(System.Threading.CancellationToken cancellationToken = default)
            => PolicySourceAsyncEnumerable.Empty(cancellationToken);
    }
}
