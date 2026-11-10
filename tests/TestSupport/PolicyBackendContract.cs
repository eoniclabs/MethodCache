using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.TestSupport;

// Shared assertions run unchanged against both real providers. Each factory creates an isolated container.
public static partial class PolicyBackendContract
{
    private static readonly string MethodId = $"[{typeof(IOrigin).Assembly.GetName().Name}]{typeof(IOrigin).FullName!.Replace('+', '.')}.ReadAsync";

    public static async Task RunAsync(Func<Origin, Action<MethodCacheBuilder>?, Task<ServiceProvider>> create, FakeTimeProvider time)
    {
        await using var first = await create(new Origin(100), null);
        await using var second = await create(new Origin(200), null);
        StartAsHost(first); StartAsHost(second);
        var a = first.GetRequiredService<IOrigin>();
        var b = second.GetRequiredService<IOrigin>();
        Assert.Equal(101, await a.ReadAsync(1));
        Assert.Equal(101, await b.ReadAsync(1));
        var receiverHandle = PolicyHandle.For(second.GetRequiredService<IPolicyRegistry>(), MethodId);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await first.GetRequiredService<IRuntimeCacheConfigurator>().UpsertAsync(MethodId, o => o.WithDuration(TimeSpan.FromSeconds(1)),
            new RuntimeOverrideMetadata { Owner = "contract" });
        time.Advance(TimeSpan.FromMilliseconds(100)); // triggers SQL's injected-clock polling without sleeping
        // The other instance reads the stored override when the backplane says the method changed
        await EventuallyAsync(() => receiverHandle.Current.Duration == TimeSpan.FromSeconds(1));
        Assert.Equal("contract", Assert.Single(second.GetRequiredService<IRuntimeCacheConfigurator>().GetOverrides()).Metadata!.Owner);
        // The following assertions isolate L2 entries from delayed messages evicting fresh local data
        await first.GetRequiredKeyedService<IBackplane>("MethodCache").UnsubscribeAsync();
        await second.GetRequiredKeyedService<IBackplane>("MethodCache").UnsubscribeAsync();
        // An instance that starts reads it too, and every instance with the override shares its entries
        await using var joined = await create(new Origin(300), null);
        StartAsHost(joined);
        await joined.GetRequiredKeyedService<IBackplane>("MethodCache").UnsubscribeAsync();
        Assert.Equal(TimeSpan.FromSeconds(1), PolicyHandle.For(joined.GetRequiredService<IPolicyRegistry>(), MethodId).Current.Duration);
        time.Advance(TimeSpan.FromMilliseconds(1));
        var shared = await a.ReadAsync(1);
        Assert.Equal(shared, await joined.GetRequiredService<IOrigin>().ReadAsync(1));
        time.Advance(TimeSpan.FromSeconds(2));
        var next = await a.ReadAsync(1);
        Assert.NotEqual(shared, next);
        Assert.Equal(next, await joined.GetRequiredService<IOrigin>().ReadAsync(1));
        time.Advance(TimeSpan.FromMilliseconds(1));
        await first.GetRequiredService<IRuntimeCacheConfigurator>().RemoveAsync(MethodId);
        time.Advance(TimeSpan.FromMilliseconds(1));
        var restored = await a.ReadAsync(1);
        Assert.NotEqual(next, restored);
        await using var cold = await create(new Origin(400), null);
        StartAsHost(cold);
        Assert.Equal(TimeSpan.FromHours(1), PolicyHandle.For(cold.GetRequiredService<IPolicyRegistry>(), MethodId).Current.Duration);
        Assert.Equal(restored, await cold.GetRequiredService<IOrigin>().ReadAsync(1));

        var writer = first.GetRequiredService<ICacheManager>();
        var receiver = cold.GetRequiredService<ICacheManager>();
        await writer.GetOrCreateAsync<string?>("null", _ => ValueTask.FromResult<string?>(null));
        var nullHit = await receiver.TryGetAsync<string?>("null");
        Assert.True(nullHit.Found); Assert.Null(nullHit.Value);
        await using var rejectsNull = await create(new Origin(500), builder => builder.Memory(o => o.CacheNullValues = false));
        Assert.Equal("origin", await rejectsNull.GetRequiredService<ICacheManager>().GetOrCreateAsync("null", _ => ValueTask.FromResult("origin")));
        await writer.GetOrCreateAsync("predicate", _ => ValueTask.FromResult(1));
        Assert.Equal(2, await receiver.GetOrCreateAsync("predicate", _ => ValueTask.FromResult(2), o => o.CacheWhen<int>(value => value > 1)));
        await writer.GetOrCreateAsync("local-only", _ => ValueTask.FromResult(1));
        Assert.Equal(2, await receiver.GetOrCreateAsync("local-only", _ => ValueTask.FromResult(2), o => o.WithDistributed(false)));
        await using var verifier = await create(new Origin(600), null);
        Assert.Equal(1, await verifier.GetRequiredService<ICacheManager>().GetOrCreateAsync("local-only", _ => ValueTask.FromResult(3)));

        await writer.GetOrCreateAsync("version", _ => ValueTask.FromResult(7), o => o.WithVersion(2));
        Assert.True((await verifier.GetRequiredService<ICacheManager>().TryGetAsync<int>("version", version: 2)).Found);
        var result = await writer.InvalidateKeysAsync(["version", "version"], version: 2);
        Assert.Equal(1, result.Requested); Assert.Equal(1, result.Local); Assert.Equal(1, result.Distributed);
        Assert.Equal(0, result.Queued); Assert.Equal(1, result.Published); Assert.Equal(0, result.PublicationQueued);
    }

    /// <summary>What a generic host runs in StartAsync: among others, it reads the runtime overrides every instance applies.</summary>
    public static void StartAsHost(ServiceProvider node) => node.GetRequiredService<IStartupValidator>().Validate();

    internal static async Task EventuallyAsync(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("The condition did not become true.");
            await Task.Delay(10);
        }
    }

    public interface IOrigin
    {
        [Cache(Duration = "1h")] Task<int> ReadAsync(int id);
        [Cache] int Read(int id);
        [Cache(Group = "native-enabled", Enabled = true)] Task<int> EnabledAsync(int id);
        [Cache(Group = "native-disabled", Enabled = false)] Task<int> DisabledAsync(int id);
        [Cache(Duration = "10s")] Task<int> FixedAsync(int id);
        [Cache(Duration = "5s", SlidingExpiration = "1s")] Task<int> SlidingAsync(int id);
    }
    public sealed class Origin(int basis) : IOrigin
    {
        private int _calls;
        private int _syncCalls;
        public int Calls => Volatile.Read(ref _calls);
        public int SyncCalls => Volatile.Read(ref _syncCalls);
        public Func<int, Task<int>>? OnCall { get; init; }
        public Task<int> ReadAsync(int id)
        {
            var call = Interlocked.Increment(ref _calls);
            return OnCall?.Invoke(call) ?? Task.FromResult(basis + call);
        }
        public int Read(int id) => basis + Interlocked.Increment(ref _syncCalls);
        public Task<int> EnabledAsync(int id) => ReadAsync(id);
        public Task<int> DisabledAsync(int id) => ReadAsync(id);
        public Task<int> FixedAsync(int id) => ReadAsync(id);
        public Task<int> SlidingAsync(int id) => ReadAsync(id);
    }
}
