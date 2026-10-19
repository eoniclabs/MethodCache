using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using MethodCache.Core.PolicyPipeline.Resolution;
using MethodCache.Policies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.TestSupport;

public static partial class PolicyBackendContract
{
    public static async Task RunAcceptanceAsync(string scenario,
        Func<Origin, Action<MethodCacheBuilder>?, Task<ServiceProvider>> create, FakeTimeProvider time)
    {
        switch (scenario)
        {
            case "lookup": await LookupAsync(create); break;
            case "equivalent": await EqualPolicyAsync(create); break;
            case "reload": await ReloadAsync(create, time); break;
            case "specificity": await SpecificityAsync(create); break;
            case "disabled": await DisabledAsync(create, time, refresh: false); break;
            case "refresh": await DisabledAsync(create, time, refresh: true); break;
            case "custom": await CustomAsync(create); break;
            case "sync": await SyncAsync(create); break;
            case "unready-sync": await UnreadySyncAsync(create); break;
            case "lost-message": await LostMessageAsync(create, time); break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
    }

    private static async Task LookupAsync(Func<Origin, Action<MethodCacheBuilder>?, Task<ServiceProvider>> create)
    {
        await using var first = await create(new Origin(100), null);
        await using var second = await create(new Origin(200), null);
        await SilenceAsync(first); await SilenceAsync(second);
        Assert.Equal(101, await first.GetRequiredService<IOrigin>().ReadAsync(1));
        var key = MethodCacheKeys.For(typeof(IOrigin), nameof(IOrigin.ReadAsync), MethodCacheKeys.Part(1));
        var cache = second.GetRequiredService<ICacheManager>();
        var generated = await cache.TryGetAsync<int>(key, default);
        Assert.True(generated.Found);
        Assert.Equal(101, generated.Value);
        await first.GetRequiredService<ICacheManager>().GetOrCreateAsync("manual-lookup", _ => new ValueTask<int>(42), options => options.WithTags("lookup-tag"));
        Assert.True((await cache.TryGetAsync<int>("manual-lookup", default)).Found);
        Assert.Contains("lookup-tag", second.GetRequiredService<ICacheAdministrator>().GetEntry("manual-lookup")!.Tags);
        await cache.InvalidateTagsAsync(["lookup-tag"], default);
        Assert.False((await cache.TryGetAsync<int>("manual-lookup", default)).Found);
    }

    private static PolicyHandle Handle(ServiceProvider node) => PolicyHandle.For(node.GetRequiredService<IPolicyRegistry>(), MethodId);
    private static Task SilenceAsync(ServiceProvider node)
    {
        node.GetRequiredService<PolicyMessageGate>().DropMessages = true;
        return Task.CompletedTask;
    }

    private static async Task EqualPolicyAsync(Func<Origin, Action<MethodCacheBuilder>?, Task<ServiceProvider>> create)
    {
        var a = new Origin(100); var b = new Origin(200);
        var duration = Configuration("5m");
        var tags = new ConfigurationManager();
        tags.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"MethodCache:Services:{MethodId}:Tags:0"] = "a",
            [$"MethodCache:Services:{MethodId}:Tags:1"] = "b"
        });
        await using var first = await create(a, builder =>
        {
            builder.FromConfiguration(duration, "MethodCache").FromConfiguration(tags, "MethodCache");
            builder.Method((IOrigin r) => r.ReadAsync(0)).Configure(o => o.WithTags("a", "b"));
        });
        await using var second = await create(b, builder =>
        {
            builder.FromConfiguration(tags, "MethodCache").FromConfiguration(duration, "MethodCache");
            builder.Method((IOrigin r) => r.ReadAsync(0)).Configure(o => o.WithTags("b", "a", "a"));
        });
        await SilenceAsync(first); await SilenceAsync(second);
        Assert.Equal(Handle(first).Current.Identity, Handle(second).Current.Identity);
        Assert.Equal(101, await first.GetRequiredService<IOrigin>().ReadAsync(1));
        Assert.Equal(101, await second.GetRequiredService<IOrigin>().ReadAsync(1));
        Assert.Equal(0, b.Calls); // actual shared backend reuse, not two coincidentally equal factories
        var before = Handle(first).Current;
        await first.GetRequiredService<IRuntimeCacheConfigurator>().UpsertAsync(MethodId,
            o => o.WithTags("b", "a"), new RuntimeOverrideMetadata { Owner = "audit-only" });
        Assert.Same(before.Generation, Handle(first).Current.Generation);
        Assert.Equal(before.Identity, Handle(first).Current.Identity);
        Assert.False(Handle(first).Current.PrivateIdentity);
        Assert.Equal(101, await first.GetRequiredService<IOrigin>().ReadAsync(1));
        Assert.Equal(1, a.Calls);
    }

    private static ConfigurationManager Configuration(string duration)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { [$"MethodCache:Services:{MethodId}:Duration"] = duration });
        return configuration;
    }

    private static async Task ReloadAsync(Func<Origin, Action<MethodCacheBuilder>?, Task<ServiceProvider>> create, FakeTimeProvider time)
    {
        var a = new Origin(100); var b = new Origin(200);
        var ca = Configuration("1h"); var cb = Configuration("1h");
        await using var first = await create(a, builder => builder.FromConfiguration(ca, "MethodCache"));
        await using var second = await create(b, builder => builder.FromConfiguration(cb, "MethodCache"));
        await SilenceAsync(first); await SilenceAsync(second);
        var readerA = first.GetRequiredService<IOrigin>(); var readerB = second.GetRequiredService<IOrigin>();
        Assert.Equal(101, await readerA.ReadAsync(1)); Assert.Equal(101, await readerB.ReadAsync(1));
        ca[$"MethodCache:Services:{MethodId}:Duration"] = "1s"; ((IConfigurationRoot)ca).Reload();
        await ConfirmReloadAsync(first);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), Handle(first).Current.Duration);
        Assert.Equal(201, await readerB.ReadAsync(2)); // B writes with its old shared identity after A reloads
        Assert.Equal(102, await readerA.ReadAsync(2));
        Assert.NotEqual(Handle(first).Current.Identity, Handle(second).Current.Identity);
        time.Advance(TimeSpan.FromSeconds(2)); Assert.Equal(103, await readerA.ReadAsync(2));
        cb[$"MethodCache:Services:{MethodId}:Duration"] = "1s"; ((IConfigurationRoot)cb).Reload();
        await ConfirmReloadAsync(second);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(Handle(first).Current.Identity, Handle(second).Current.Identity);
        Assert.Equal(104, await readerA.ReadAsync(3)); Assert.Equal(104, await readerB.ReadAsync(3));
        Assert.Equal(1, b.Calls);
    }

    private static async Task ConfirmReloadAsync(ServiceProvider node)
    {
        // Reload publishes local policy synchronously; native tombstone I/O is owned asynchronous work.
        // Observe its actual completion before asserting L2 reuse, rather than advancing time as a wait.
        Console.WriteLine($"Reload generation ready before confirmation: {Handle(node).Current.Generation!.DistributedReady}");
        await ((PolicyRegistry)node.GetRequiredService<IPolicyRegistry>()).Invalidation(MethodId).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(Handle(node).Current.Generation!.DistributedReady);
        Assert.Equal(new InvalidationQueues(0, 0), ((InMemoryCacheManager)node.GetRequiredService<ICacheManager>()).GetInvalidationQueues());
    }

    private static async Task SpecificityAsync(Func<Origin, Action<MethodCacheBuilder>?, Task<ServiceProvider>> create)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MethodCache:Defaults:Enabled"] = "false"
        });
        var origin = new Origin(100);
        await using var node = await create(origin, builder =>
        {
            builder.Group("native-enabled", o => o.WithEnabled(false));
            builder.Group("native-disabled", o => o.WithEnabled(true));
            builder.FromConfiguration(configuration, "MethodCache");
        });
        var reader = node.GetRequiredService<IOrigin>();
        Assert.Equal(101, await reader.EnabledAsync(1)); Assert.Equal(101, await reader.EnabledAsync(1));
        Assert.Equal(102, await reader.DisabledAsync(1)); Assert.Equal(103, await reader.DisabledAsync(1));
        Assert.Equal(3, origin.Calls);
    }

    private static async Task DisabledAsync(Func<Origin, Action<MethodCacheBuilder>?, Task<ServiceProvider>> create,
        FakeTimeProvider time, bool refresh)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldCall = refresh ? 2 : 1;
        var origin = new Origin(100)
        {
            OnCall = call =>
        {
            if (call == heldCall) { started.TrySetResult(); return release.Task; }
            return Task.FromResult(100 + call);
        }
        };
        Action<MethodCacheBuilder>? configure = refresh
            ? builder => builder.Method((IOrigin r) => r.ReadAsync(0)).Configure(o => o.WithDuration(TimeSpan.FromSeconds(3)).WithRefreshAhead(TimeSpan.FromSeconds(1))) : null;
        await using var node = await create(origin, configure);
        await SilenceAsync(node);
        var reader = node.GetRequiredService<IOrigin>();
        Task<int> old;
        try
        {
            if (refresh)
            {
                Assert.Equal(101, await reader.ReadAsync(1));
                time.Advance(TimeSpan.FromMilliseconds(2500));
                Assert.Equal(101, await reader.ReadAsync(1));
                await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
                time.Advance(TimeSpan.FromMilliseconds(600));
                old = reader.ReadAsync(1); // joins the expired entry's held refresh, so its completion is observable
            }
            else old = reader.ReadAsync(1);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var store = node.GetRequiredService<GatedNativePolicyStore>();
            var writes = store.ValueWrites;
            Assert.Equal(refresh ? 1 : 0, writes);
            await node.GetRequiredService<IRuntimeCacheConfigurator>().UpsertAsync(MethodId, o => o.WithEnabled(false));
            var uncached = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => reader.ReadAsync(1)));
            Assert.Equal(8, uncached.Distinct().Count());
            Assert.Equal(heldCall + 8, origin.Calls);
            Assert.Equal(writes, store.ValueWrites);
            release.TrySetResult(100 + heldCall);
            Assert.Equal(100 + heldCall, await old.WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Equal(writes, store.ValueWrites); // neither bypasses nor the retired load/refresh attempt a value write
            Assert.Equal(100 + heldCall + 9, await reader.ReadAsync(1));
            await using var cold = await create(new Origin(500), configure);
            await SilenceAsync(cold);
            Assert.Equal(501, await cold.GetRequiredService<IOrigin>().ReadAsync(1)); // old load/refresh never republished shared data
            await node.GetRequiredService<IRuntimeCacheConfigurator>().UpsertAsync(MethodId, o => o.WithEnabled(true));
            time.Advance(TimeSpan.FromMilliseconds(1));
            var current = await reader.ReadAsync(1);
            Assert.Equal(100 + heldCall + 10, current); Assert.Equal(current, await reader.ReadAsync(1));
        }
        finally { release.TrySetResult(100 + heldCall); }
    }

    private static async Task CustomAsync(Func<Origin, Action<MethodCacheBuilder>?, Task<ServiceProvider>> create)
    {
        var custom = new RecordingManager(); var origin = new Origin(100);
        await using var node = await create(origin, builder =>
        {
            builder.Services.AddSingleton<ICacheManager>(custom);
            builder.Method((IOrigin r) => r.ReadAsync(0)).Configure(o => o.WithVersion(2).WithDistributed(false).WithTags("native:{id}"));
        });
        var reader = node.GetRequiredService<IOrigin>();
        Assert.Equal(101, await reader.ReadAsync(1)); Assert.Equal(102, await reader.ReadAsync(1));
        Assert.Equal(2, custom.Calls); Assert.Equal(2, custom.LastOptions!.Version);
        Assert.False(custom.LastOptions.Distributed); Assert.Contains("native:1", custom.LastOptions.Tags);
        Assert.Equal(101, reader.Read(1)); Assert.Equal(102, reader.Read(1));
        Assert.Equal(2, custom.Calls); // sync must bypass an asynchronous custom manager
        await using var noop = await create(new Origin(200), builder => builder.Services.AddSingleton<ICacheManager>(new NoOpCacheManager()));
        Assert.Equal(201, await noop.GetRequiredService<IOrigin>().ReadAsync(1));
        Assert.Equal(202, await noop.GetRequiredService<IOrigin>().ReadAsync(1));
    }

    private static async Task SyncAsync(Func<Origin, Action<MethodCacheBuilder>?, Task<ServiceProvider>> create)
    {
        var a = new Origin(100); var b = new Origin(200);
        await using var first = await create(a, null); await using var second = await create(b, null);
        var readerA = first.GetRequiredService<IOrigin>(); var readerB = second.GetRequiredService<IOrigin>();
        Assert.Equal(101, readerA.Read(1)); Assert.Equal(101, readerA.Read(1));
        Assert.Equal(201, readerB.Read(1)); Assert.Equal(1, a.SyncCalls); Assert.Equal(1, b.SyncCalls);
        Assert.Equal(101, await readerA.ReadAsync(1)); Assert.Equal(101, await readerB.ReadAsync(1));
        Assert.Equal(0, b.Calls); // native async reuse exists; sync's local/factory behavior is deliberate
    }

    private static async Task UnreadySyncAsync(Func<Origin, Action<MethodCacheBuilder>?, Task<ServiceProvider>> create)
    {
        var origin = new Origin(100);
        await using var node = await create(origin, null);
        var reader = node.GetRequiredService<IOrigin>();
        var store = node.GetRequiredService<GatedNativePolicyStore>();
        var asynchronous = reader.ReadAsync(1);
        try
        {
            await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(asynchronous.IsCompleted);
            Assert.Equal(101, await Task.Run(() => reader.Read(1)).WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Equal(101, reader.Read(1));
            Assert.False(asynchronous.IsCompleted);
            Assert.Equal(0, store.SyncReads);
            Assert.Equal(1, origin.SyncCalls);
        }
        finally { store.Release.TrySetResult(); }
        Assert.Equal(101, await asynchronous.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    private static async Task LostMessageAsync(Func<Origin, Action<MethodCacheBuilder>?, Task<ServiceProvider>> create, FakeTimeProvider time)
    {
        await using var first = await create(new Origin(100), builder => builder.Method((IOrigin r) => r.ReadAsync(0)).Configure(o => o.WithDuration(TimeSpan.FromHours(1))));
        await using var second = await create(new Origin(200), builder => builder.Method((IOrigin r) => r.ReadAsync(0)).Configure(o => o.WithDuration(TimeSpan.FromHours(1))));
        var a = first.GetRequiredService<IOrigin>(); var b = second.GetRequiredService<IOrigin>();
        var gate = second.GetRequiredService<PolicyMessageGate>();
        await SilenceAsync(second);
        Assert.Equal(101, await a.ReadAsync(1)); Assert.Equal(101, await b.ReadAsync(1));
        await gate.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var before = Handle(second).Current;
        time.Advance(TimeSpan.FromMilliseconds(1));
        await first.GetRequiredService<IRuntimeCacheConfigurator>().UpsertAsync(MethodId, o => o.WithDuration(TimeSpan.FromSeconds(1)));
        time.Advance(TimeSpan.FromMilliseconds(100)); // triggers SQL's injected-clock polling without sleeping
        await gate.MethodDropped.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(101, await b.ReadAsync(1)); Assert.Same(before.Generation, Handle(second).Current.Generation);
        Assert.Equal(TimeSpan.FromHours(1), Handle(second).Current.Duration);
        time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        Assert.Equal(201, await b.ReadAsync(1)); Assert.Equal(102, await a.ReadAsync(1));
    }

    private sealed class RecordingManager : ICacheManager
    {
        private readonly NoOpCacheManager _noOp = new();
        internal int Calls;
        internal CacheEntryOptions? LastOptions;
        public async ValueTask<T> GetOrCreateAsync<TState, T>(string key, TState state, Func<TState, CancellationToken, ValueTask<T>> factory,
            CacheEntryOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++; LastOptions = options;
            await Task.Yield();
            return await _noOp.GetOrCreateAsync(key, state, factory, options, cancellationToken);
        }
        public ValueTask<CacheLookupResult<T>> TryGetAsync<T>(string key, int? version = null, CancellationToken cancellationToken = default)
            => _noOp.TryGetAsync<T>(key, version, cancellationToken);
        public ValueTask<InvalidationResult> InvalidateKeysAsync(IEnumerable<string> keys, int? version = null, CancellationToken cancellationToken = default)
            => _noOp.InvalidateKeysAsync(keys, version, cancellationToken);
        public ValueTask<InvalidationResult> InvalidateTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
            => _noOp.InvalidateTagsAsync(tags, cancellationToken);
    }
}

// A connected receiver that loses frames. Unlike unsubscription, this controls the delivery interleaving
// without conflating the scenario with initial/reconnect lifecycle work. The actual native publisher,
// subscription acknowledgement and SQL poll still run; the dropped method frame is explicitly observed.
internal sealed class PolicyMessageGate : IBackplane, IAsyncDisposable
{
    private readonly IBackplane _inner;
    internal volatile bool DropMessages;
    private int _disposed;
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource MethodDropped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PolicyMessageGate(IBackplane inner) => _inner = inner;
    internal static void Decorate(IServiceCollection services)
    {
        var descriptor = services.Last(d => d.IsKeyedService && d.ServiceType == typeof(IBackplane) && Equals(d.ServiceKey, "MethodCache"));
        services.Remove(descriptor);
        services.AddSingleton(provider => new PolicyMessageGate((IBackplane)(descriptor.KeyedImplementationInstance
            ?? descriptor.KeyedImplementationFactory!(provider, "MethodCache"))));
        services.AddKeyedSingleton<IBackplane>("MethodCache", static (provider, _) => provider.GetRequiredService<PolicyMessageGate>());
    }
    public string InstanceId => _inner.InstanceId;
    public BackplaneState ConnectionState => _inner.ConnectionState;
    public Task PublishInvalidationAsync(string key, CancellationToken cancellationToken = default) => _inner.PublishInvalidationAsync(key, cancellationToken);
    public Task PublishTagInvalidationAsync(string tag, CancellationToken cancellationToken = default) => _inner.PublishTagInvalidationAsync(tag, cancellationToken);
    public Task PublishMethodInvalidationAsync(string methodId, CancellationToken cancellationToken = default) => _inner.PublishMethodInvalidationAsync(methodId, cancellationToken);
    public Task PublishClearAsync(CancellationToken cancellationToken = default) => _inner.PublishClearAsync(cancellationToken);
    public async Task SubscribeAsync(Func<BackplaneMessage, Task> callback, CancellationToken cancellationToken = default)
    {
        try
        {
            await _inner.SubscribeAsync(message =>
            {
                if (!DropMessages) return callback(message);
                if (message.Type == BackplaneMessageType.MethodInvalidation) MethodDropped.TrySetResult();
                return Task.CompletedTask;
            }, cancellationToken);
            Ready.TrySetResult();
        }
        catch (Exception error) { Ready.TrySetException(error); throw; }
    }
    public Task UnsubscribeAsync(CancellationToken cancellationToken = default) => _inner.UnsubscribeAsync(cancellationToken);
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_inner is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
        else if (_inner is IDisposable disposable) disposable.Dispose();
        else await _inner.UnsubscribeAsync();
    }
}
