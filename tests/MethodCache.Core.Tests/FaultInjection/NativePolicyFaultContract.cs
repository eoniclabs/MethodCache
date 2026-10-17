using System.Buffers.Binary;
using System.Collections.Concurrent;
using MethodCache.Core.Runtime.Distributed;
using MethodCache.Extensibility;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.TestSupport;

// The same controlled faults run over the model and both real providers. Successful operations
// always reach the supplied backend; this is not a replacement in-memory cache for native tests.
internal static class NativePolicyFaultContract
{
    internal static async Task RunAsync(string scenario, IAtomicInvalidationStore backend, IBackplane backplane,
        Action? afterHold = null, bool includeEarlierStamp = false)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new FaultStore(backend);
        var publication = new FaultPublication(backplane);
        await using var layer = new DistributedCacheLayer(store, new JsonCacheSerializer(),
            new DistributedCacheOptions { KeyPrefix = "policy-fault:" + Guid.NewGuid().ToString("N") + ":", ClockSkewTolerance = TimeSpan.Zero }, publication, time);
        using var memory = new InMemoryCacheManager(null, time, layer);
        ICacheManager cache = memory;
        try
        {
            switch (scenario)
            {
                case "partial":
                    store.FailOnce = static key => key.EndsWith("bad", StringComparison.Ordinal);
                    publication.FailOnce = "good";
                    var result = await cache.InvalidateTagsAsync(["good", "bad", "third", "good"]);
                    Assert.Equal(new InvalidationResult { Requested = 3, Local = 3, Distributed = 2, Queued = 1, Published = 1, PublicationQueued = 1 }, result);
                    Assert.Equal(new InvalidationQueues(1, 1), memory.GetInvalidationQueues());
                    await layer.FlushAsync(force: true);
                    await layer.FlushPublicationsAsync();
                    Assert.Equal(new InvalidationQueues(0, 0), memory.GetInvalidationQueues());
                    Assert.Equal(3, store.Confirmed.Count);
                    Assert.Equal(3, publication.Confirmed);
                    break;
                case "cancellation":
                    await cache.GetOrCreateAsync("held", (_, _) => ValueTask.FromResult(1));
                    if (includeEarlierStamp)
                        await store.WriteInvalidationStampAsync("fixture-prior:" + Guid.NewGuid().ToString("N"), time.GetUtcNow().UtcTicks, TimeSpan.FromHours(1));
                    store.Hold = true;
                    using (var cancellation = new CancellationTokenSource())
                    {
                        var invalidating = cache.InvalidateKeysAsync(["held"], cancellation.Token).AsTask();
                        await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
                        afterHold?.Invoke();
                        Assert.False(memory.TryGetFreshForLookup<int>("held").Found);
                        cancellation.Cancel();
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invalidating);
                        Assert.Equal(0, store.HeldConfirmed);
                        store.Release.TrySetResult();
                        await layer.DisposeAsync();
                        Assert.Equal(1, store.HeldConfirmed);
                        Assert.Equal(new InvalidationQueues(0, 0), memory.GetInvalidationQueues());
                    }
                    break;
                case "validation":
                    await cache.GetOrCreateAsync("retained", (_, _) => ValueTask.FromResult(1));
                    await Assert.ThrowsAsync<ArgumentException>(() => cache.InvalidateKeysAsync(["retained", " "]).AsTask());
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.InvalidateKeysAsync(["retained"], new CancellationToken(true)).AsTask());
                    Assert.True(memory.TryGetFreshForLookup<int>("retained").Found);
                    Assert.True((await cache.TryGetAsync<int>("retained")).Found);
                    Assert.Empty(store.Confirmed);
                    Assert.Equal(new InvalidationResult(), await cache.InvalidateKeysAsync([]));
                    var unique = await cache.InvalidateKeysAsync(["retained", "retained", "absent"]);
                    Assert.Equal(2, unique.Requested);
                    Assert.Equal(2, unique.Local);
                    Assert.Equal(2, unique.Distributed);
                    Assert.Equal(0, unique.Queued);
                    Assert.Equal(2, store.Confirmed.Count);
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(scenario));
            }
            // Read the actual native metadata, not the wrapper's in-memory receipt alone.
            foreach (var write in store.Confirmed)
            {
                var bytes = Assert.IsType<byte[]>(await backend.GetAsync(write.Key));
                Assert.Equal(write.Timestamp, BinaryPrimitives.ReadInt64LittleEndian(bytes));
            }
        }
        finally
        {
            // A failed assertion must not leave owned shutdown waiting forever for this test gate.
            store.Release.TrySetResult();
        }
    }

    private sealed class FaultStore(IAtomicInvalidationStore inner) : IAtomicInvalidationStore
    {
        internal Func<string, bool>? FailOnce;
        internal bool Hold;
        internal int HeldConfirmed;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<(string Key, long Timestamp)> Confirmed { get; } = new();
        public async Task WriteInvalidationStampAsync(string key, long timestamp, TimeSpan retention, CancellationToken cancellationToken = default)
        {
            if (FailOnce?.Invoke(key) == true)
            {
                FailOnce = null;
                throw new InvalidOperationException("Controlled native stamp failure.");
            }
            var held = Hold;
            if (held) { Started.TrySetResult(); await Release.Task; }
            await inner.WriteInvalidationStampAsync(key, timestamp, retention, cancellationToken);
            Confirmed.Enqueue((key, timestamp));
            if (held) Interlocked.Increment(ref HeldConfirmed);
        }
        public byte[]? Get(string key) => inner.Get(key);
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => inner.GetAsync(key, token);
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => inner.Set(key, value, options);
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => inner.SetAsync(key, value, options, token);
        public void Refresh(string key) => inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => inner.RefreshAsync(key, token);
        public void Remove(string key) => inner.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) => inner.RemoveAsync(key, token);
    }

    private sealed class FaultPublication(IBackplane inner) : IBackplane
    {
        internal string? FailOnce;
        internal int Confirmed;
        public string InstanceId => inner.InstanceId;
        public Task PublishInvalidationAsync(string key, CancellationToken cancellationToken = default) => inner.PublishInvalidationAsync(key, cancellationToken);
        public async Task PublishTagInvalidationAsync(string tag, CancellationToken cancellationToken = default)
        {
            if (tag == FailOnce) { FailOnce = null; throw new InvalidOperationException("Controlled native publication failure."); }
            await inner.PublishTagInvalidationAsync(tag, cancellationToken);
            Interlocked.Increment(ref Confirmed);
        }
        public Task SubscribeAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken = default) => inner.SubscribeAsync(onMessage, cancellationToken);
        public Task UnsubscribeAsync(CancellationToken cancellationToken = default) => inner.UnsubscribeAsync(cancellationToken);
    }
}

// Holds a real asynchronous backend read. Synchronous reads are forbidden: the generated sync path
// must use its L1/origin fallback while this operation is pending, without waiting on its Task.
internal sealed class GatedNativePolicyStore(IAtomicInvalidationStore inner, bool holdReads) : IAtomicInvalidationStore
{
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int SyncReads;
    internal int ValueWrites;
    internal static void Decorate(IServiceCollection services, bool holdReads = true)
    {
        var descriptor = services.Last(d => d.IsKeyedService && d.ServiceType == typeof(IDistributedCache) && Equals(d.ServiceKey, "MethodCache"));
        services.Remove(descriptor);
        services.AddSingleton(provider =>
            new GatedNativePolicyStore((IAtomicInvalidationStore)(descriptor.KeyedImplementationInstance ?? descriptor.KeyedImplementationFactory!(provider, "MethodCache")), holdReads));
        services.AddKeyedSingleton<IDistributedCache>("MethodCache",
            static (provider, _) => provider.GetRequiredService<GatedNativePolicyStore>());
    }
    public byte[]? Get(string key)
    {
        Interlocked.Increment(ref SyncReads);
        throw new InvalidOperationException("A sync method must not call an unbounded native store.");
    }
    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        Started.TrySetResult();
        if (holdReads) await Release.Task.WaitAsync(token);
        return await inner.GetAsync(key, token);
    }
    public Task WriteInvalidationStampAsync(string key, long timestamp, TimeSpan retention, CancellationToken cancellationToken = default)
        => inner.WriteInvalidationStampAsync(key, timestamp, retention, cancellationToken);
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    { Interlocked.Increment(ref ValueWrites); inner.Set(key, value, options); }
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    { Interlocked.Increment(ref ValueWrites); return inner.SetAsync(key, value, options, token); }
    public void Refresh(string key) => inner.Refresh(key);
    public Task RefreshAsync(string key, CancellationToken token = default) => inner.RefreshAsync(key, token);
    public void Remove(string key) => inner.Remove(key);
    public Task RemoveAsync(string key, CancellationToken token = default) => inner.RemoveAsync(key, token);
}
