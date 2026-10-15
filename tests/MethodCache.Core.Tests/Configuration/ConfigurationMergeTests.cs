using System.Collections.Concurrent;
using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Core.PolicyPipeline.Resolution;
using MethodCache.Core.PolicyPipeline.Sources;
using MethodCache.Policies;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Xunit.Abstractions;

using MsOptions = Microsoft.Extensions.Options.Options;

namespace MethodCache.Core.Tests.Configuration;

public interface IConfigMergeService
{
    [Cache] Task<int> Get(int id);
}

public class ConfigurationMergeTests(ITestOutputHelper output)
{
    public sealed class ConfigMergeService : IConfigMergeService { public Task<int> Get(int id) => Task.FromResult(id); }
    private static readonly string Id = MethodIds.For(typeof(IConfigMergeService), nameof(IConfigMergeService.Get));

    // ---------- 1. RuntimePolicySource registered once per EnsureCoreServices call ----------
    [Fact]
    public async Task RuntimeSource_IsRegisteredOnce_EvenWhenSeveralAddMethodsAreCalled()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigMergeService, ConfigMergeService>();
        services.AddSingleton<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(new MethodCache.TestSupport.AtomicTestCache());
        services.AddMethodCache();
        services.AddMethodCacheFromConfiguration(new ConfigurationBuilder().Build());
        services.AddMethodCacheDistributedCache(sp => sp.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>());

        await using var sp = services.BuildServiceProvider();
        var runtimeSources = sp.GetServices<PolicySourceRegistration>().Count(r => r.Source is RuntimePolicySource);
        output.WriteLine($"runtime sources: {runtimeSources}");

        // Each extra subscription applies every change again and raises PolicyChanged again
        var resolver = sp.GetRequiredService<PolicyResolver>();
        await resolver.ResolveAsync(Id);
        var notifications = 0;
        resolver.Changed += r => { if (r.MethodId == Id) notifications++; };
        await sp.GetRequiredService<IRuntimeCacheConfigurator>().UpsertAsync(Id, b => b.WithVersion(2));
        output.WriteLine($"notifications for one upsert: {notifications}");

        Assert.Equal(1, runtimeSources);
        Assert.Equal(1, notifications);
    }

    // ---------- 2. Runtime override ExpiresAt never reaches the resolver ----------
    [Fact]
    public async Task RuntimeOverride_StopsApplying_AfterItsExpiresAt()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<IConfigMergeService, ConfigMergeService>();
        services.AddSingleton<TimeProvider>(time);
        services.AddMethodCache();
        await using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IPolicyRegistry>();
        var configurator = sp.GetRequiredService<IRuntimeCacheConfigurator>();
        registry.GetPolicy(Id);

        await configurator.UpsertAsync(Id, b => b.WithDuration(TimeSpan.FromMinutes(1)),
            new RuntimeOverrideMetadata(Owner: "ops", ExpiresAt: time.GetUtcNow().AddMilliseconds(100)));
        Assert.Equal(TimeSpan.FromMinutes(1), registry.GetPolicy(Id).Policy.Duration);

        time.Advance(TimeSpan.FromMilliseconds(99));
        Assert.Equal(TimeSpan.FromMinutes(1), registry.GetPolicy(Id).Policy.Duration);

        // The expiry timer fires on the injected clock, without any read or further change.
        time.Advance(TimeSpan.FromMilliseconds(1));
        var handle = ((PolicyRegistry)registry).GetHandle(Id);
        output.WriteLine($"after expiry: registry={registry.GetPolicy(Id).Policy.Duration}, handle={handle.Current.Duration}");
        Assert.Null(registry.GetPolicy(Id).Policy.Duration);
    }

    // ---------- 3. Snapshot path ignores declared fields (metadata replaces group; ClearTags ignored) ----------
    private static ServiceProvider GroupProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigMergeService, ConfigMergeService>();
        services.AddMethodCache(c =>
        {
            c.Group("g", o => o.WithDuration(TimeSpan.FromMinutes(10)));
            c.Method((IConfigMergeService x) => x.Get(0)).WithGroup("g").Configure(o => o.WithTags("t1"));
        });
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(true)]   // override made after the first cached call (change path)
    [InlineData(false)]  // override made at startup, before the first cached call (snapshot path)
    public async Task RuntimeOverrideWithAuditMetadata_KeepsTheGroupDefaults(bool resolveFirst)
    {
        await using var sp = GroupProvider();
        var registry = sp.GetRequiredService<IPolicyRegistry>();
        if (resolveFirst) registry.GetPolicy(Id);

        await sp.GetRequiredService<IRuntimeCacheConfigurator>().UpsertAsync(Id, b => b.WithVersion(2),
            new RuntimeOverrideMetadata(Owner: "ops", Reason: "incident"));

        var policy = registry.GetPolicy(Id).Policy;
        output.WriteLine($"resolveFirst={resolveFirst}: duration={policy.Duration}, group={(policy.Metadata.TryGetValue("group", out var g) ? g : "<lost>")}");
        Assert.Equal(TimeSpan.FromMinutes(10), policy.Duration);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RuntimeOverride_ClearTags_ClearsTheTags(bool resolveFirst)
    {
        await using var sp = GroupProvider();
        var registry = sp.GetRequiredService<IPolicyRegistry>();
        if (resolveFirst) registry.GetPolicy(Id);

        await sp.GetRequiredService<IRuntimeCacheConfigurator>().UpsertAsync(Id, CachePolicy.Empty with { Tags = Array.Empty<string>() }, CachePolicyFields.Tags | CachePolicyFields.ClearTags);

        var policy = registry.GetPolicy(Id).Policy;
        output.WriteLine($"resolveFirst={resolveFirst}: tags=[{string.Join(",", policy.Tags)}]");
        Assert.Empty(policy.Tags);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RuntimeOverride_Tags_AreAddedToTheLowerLayersTags(bool resolveFirst)
    {
        await using var sp = GroupProvider();
        var registry = sp.GetRequiredService<IPolicyRegistry>();
        if (resolveFirst) registry.GetPolicy(Id);

        await sp.GetRequiredService<IRuntimeCacheConfigurator>().UpsertAsync(Id, b => b.WithTags("t2", "t1"));

        Assert.Equal(new[] { "t2", "t1" }, registry.GetPolicy(Id).Policy.Tags);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RuntimeOverride_BuilderClearTags_ReplacesTheLowerLayersTags(bool resolveFirst)
    {
        await using var sp = GroupProvider();
        var registry = sp.GetRequiredService<IPolicyRegistry>();
        if (resolveFirst) registry.GetPolicy(Id);

        await sp.GetRequiredService<IRuntimeCacheConfigurator>().UpsertAsync(Id, b => b.ClearTags().WithTags("t2"));

        Assert.Equal(new[] { "t2" }, registry.GetPolicy(Id).Policy.Tags);
    }

    // ---------- 4. Calling AddMethodCache twice: second fluent source replaces the first one's layer ----------
    [Fact]
    public async Task TwoAddMethodCacheCalls_BothDefaultPoliciesApply()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigMergeService, ConfigMergeService>();
        services.AddMethodCache(c => c.Defaults(o => o.WithDuration(TimeSpan.FromMinutes(5))));
        services.AddMethodCache(c => c.Defaults(o => o.WithTags("from-second-call")));
        services.AddMethodCache(c => c.Method((IConfigMergeService x) => x.Get(0)).WithVersion(1));
        await using var sp = services.BuildServiceProvider();

        var policy = sp.GetRequiredService<IPolicyRegistry>().GetPolicy(Id).Policy;
        output.WriteLine($"duration={policy.Duration}, tags=[{string.Join(",", policy.Tags)}], version={policy.Version}");
        Assert.Equal(TimeSpan.FromMinutes(5), policy.Duration);
        Assert.Contains("from-second-call", policy.Tags);
    }

    [Fact]
    public async Task TwoConfigurationSections_BothApply()
    {
        var a = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"A:Services:{Id}:Duration"] = "00:05:00",
        }).Build();
        var b = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["B:Defaults:Tags:0"] = "from-b",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfigMergeService, ConfigMergeService>();
        services.AddMethodCache();
        services.AddMethodCacheFromConfiguration(a, "A");
        services.AddMethodCacheFromConfiguration(b, "B");
        await using var sp = services.BuildServiceProvider();
        var policy = sp.GetRequiredService<IPolicyRegistry>().GetPolicy(Id).Policy;
        output.WriteLine($"duration={policy.Duration}, tags=[{string.Join(",", policy.Tags)}]");
        Assert.Equal(TimeSpan.FromMinutes(5), policy.Duration);
        Assert.Contains("from-b", policy.Tags);
    }

    // ---------- 5. Synchronous container disposal ----------
    [Fact]
    public void SyncDispose_DoesNotThrow()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"MethodCache:Services:{Id}:Duration"] = "00:05:00",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfigMergeService, ConfigMergeService>();
        services.AddSingleton<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(new MethodCache.TestSupport.AtomicTestCache());
        services.AddMethodCache(c => c.Defaults(o => o.WithDuration(TimeSpan.FromMinutes(1))));
        services.AddMethodCacheFromConfiguration(config);
        services.AddMethodCacheDistributedCache(sp => sp.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>());
        var sp = services.BuildServiceProvider();
        sp.GetRequiredService<ICacheManager>();
        sp.GetRequiredService<IPolicyRegistry>().GetPolicy(Id);
        sp.GetRequiredService<ICacheAdministrator>();
        sp.Dispose();
    }

    // ---------- 9. Concurrent changes from two watched sources: last published result can be stale ----------
    [Fact]
    public async Task ConcurrentSourceChanges_FinalResolutionReflectsBothLatestLayers()
    {
        const int N = 20000;
        var failures = 0;
        for (var round = 0; round < 20; round++)
        {
            var a = new ChannelSource("a");
            var b = new ChannelSource("b");
            var resolver = new PolicyResolver(new[] { new PolicySourceRegistration(a, 50), new PolicySourceRegistration(b, 60) });
            await resolver.ResolveAsync(Id);
            var ta = Task.Run(() => { for (var i = 1; i <= N; i++) a.Emit(Id, CachePolicy.Empty with { Duration = TimeSpan.FromSeconds(i) }, CachePolicyFields.Duration); a.Complete(); });
            var tb = Task.Run(() => { for (var i = 1; i <= N; i++) b.Emit(Id, CachePolicy.Empty with { Version = i }, CachePolicyFields.Version); b.Complete(); });
            await Task.WhenAll(ta, tb);
            await Task.WhenAll(a.Drained.Task, b.Drained.Task);

            // Both sources are drained; give the resolver a bounded time to publish, rather than a fixed sleep
            var r = await resolver.ResolveAsync(Id);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((r.Policy.Duration != TimeSpan.FromSeconds(N) || r.Policy.Version != N) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(5);
                r = await resolver.ResolveAsync(Id);
            }

            if (r.Policy.Duration != TimeSpan.FromSeconds(N) || r.Policy.Version != N)
            {
                failures++;
                output.WriteLine($"round {round}: duration={r.Policy.Duration}, version={r.Policy.Version}, resultVersion={r.Version}");
            }
            await resolver.DisposeAsync();
        }
        Assert.Equal(0, failures);
    }

    private sealed class ChannelSource(string id) : MethodCache.Policies.IPolicySource
    {
        private readonly System.Threading.Channels.Channel<MethodCache.Policies.PolicyChange> _ch = System.Threading.Channels.Channel.CreateUnbounded<MethodCache.Policies.PolicyChange>();
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string SourceId => id;
        public void Emit(string methodId, CachePolicy policy, CachePolicyFields fields) =>
            _ch.Writer.TryWrite(new MethodCache.Policies.PolicyChange(id, methodId, new MethodCache.Policies.CachePolicyDelta(fields, CachePolicyFields.None, policy), MethodCache.Policies.PolicyChangeReason.Updated, DateTimeOffset.UtcNow));
        public void Complete() => _ch.Writer.TryComplete();
        public Task<IReadOnlyCollection<MethodCache.Policies.PolicySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<MethodCache.Policies.PolicySnapshot>>(Array.Empty<MethodCache.Policies.PolicySnapshot>());
        public async IAsyncEnumerable<MethodCache.Policies.PolicyChange> WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var c in _ch.Reader.ReadAllAsync(cancellationToken)) yield return c;
            Drained.TrySetResult();
        }
    }
}
