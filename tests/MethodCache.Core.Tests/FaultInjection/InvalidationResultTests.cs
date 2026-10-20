using MethodCache.Core.Runtime.Distributed;
using MethodCache.Extensibility;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.FaultInjection;

public class InvalidationResultTests
{
    [Fact]
    public async Task PartialStorageAndPublicationFailures_HaveIndependentCounters_AndRecover()
    {
        var time = new FakeTimeProvider();
        var store = new MethodCache.TestSupport.AtomicTestCache();
        var backend = new ChaosDistributedCache(store, time).Inject(ChaosOperations.Set,
            ChaosFault.Throw() with { Keys = static key => key.EndsWith("bad", StringComparison.Ordinal), Times = 1 });
        var backplane = new FailedPublication();
        await using var layer = new DistributedCacheLayer(backend, new JsonCacheSerializer(),
            new DistributedCacheOptions { ClockSkewTolerance = TimeSpan.Zero }, backplane, time);
        using var memory = new InMemoryCacheManager(null, time, layer);
        ICacheManager cache = memory;
        var result = await cache.InvalidateTagsAsync(["good", "bad", "third", "good"]);
        Assert.Equal(3, result.Requested);
        Assert.Equal(3, result.Local);
        Assert.Equal(2, result.Distributed);
        Assert.Equal(1, result.Queued);
        Assert.Equal(1, result.Published);
        Assert.Equal(1, result.PublicationQueued);
        // The public constructor, which an ICacheAdministrator of your own uses, builds the same value
        Assert.Equal(new InvalidationQueues { Queued = 1, PublicationQueued = 1 }, memory.GetInvalidationQueues());
        Assert.False(await layer.CheckAvailableAsync(CancellationToken.None));
        await layer.FlushAsync(force: true);
        Assert.Equal(0, memory.GetInvalidationQueues().Queued);
        await layer.FlushPublicationsAsync();
        Assert.Equal(new InvalidationQueues(0, 0), memory.GetInvalidationQueues());
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(await layer.CheckAvailableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConfirmedRemovalMetadata_CountsAsDistributed_EvenIfPhysicalDeletionFails()
    {
        var time = new FakeTimeProvider();
        var store = new MethodCache.TestSupport.AtomicTestCache();
        var backend = new ChaosDistributedCache(store, time).Inject(ChaosOperations.Remove,
            ChaosFault.Throw() with { Times = 1 });
        await using var layer = new DistributedCacheLayer(backend, new JsonCacheSerializer(), new DistributedCacheOptions(), timeProvider: time);
        using var memory = new InMemoryCacheManager(null, time, layer);
        var result = await ((ICacheManager)memory).InvalidateKeysAsync(["key"]);
        Assert.Equal(1, result.Distributed);
        Assert.Equal(0, result.Queued);
        Assert.Equal(0, result.Published);
        Assert.Equal(0, result.PublicationQueued);
    }

    [Fact]
    public async Task InvalidBatch_AndCancellationBeforeMutation_LeaveAllValuesUntouched()
    {
        using var memory = new InMemoryCacheManager();
        ICacheManager cache = memory;
        await cache.GetOrCreateAsync("key", _ => ValueTask.FromResult(1));
        await Assert.ThrowsAsync<ArgumentException>(async () => await cache.InvalidateKeysAsync(["key", " "]));
        Assert.True((await cache.TryGetAsync<int>("key")).Found);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cache.InvalidateKeysAsync(["key"], cancellationToken: new CancellationToken(true)));
        Assert.True((await cache.TryGetAsync<int>("key")).Found);
        Assert.Equal(new InvalidationResult(), await cache.InvalidateKeysAsync([]));
        var result = await cache.InvalidateKeysAsync(["key", "key", "absent"]);
        Assert.Equal(2, result.Requested);
        Assert.Equal(2, result.Local);
        Assert.Equal(0, result.Distributed);
    }

    [Fact]
    public async Task CancellationAfterLocalMutation_StopsOnlyTheWait_AndOwnedRecoveryFinishes()
    {
        var time = new FakeTimeProvider();
        var backend = new HeldStampStore();
        await using var layer = new DistributedCacheLayer(backend, new JsonCacheSerializer(), new DistributedCacheOptions(), timeProvider: time);
        using var memory = new InMemoryCacheManager(null, time, layer);
        ICacheManager cache = memory;
        await cache.GetOrCreateAsync("key", _ => ValueTask.FromResult(1));
        using var cancellation = new CancellationTokenSource();
        var invalidating = cache.InvalidateKeysAsync(["key"], cancellationToken: cancellation.Token).AsTask();
        await backend.Started.Task;
        Assert.False(memory.TryGetFreshForLookup<int>("key").Found);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invalidating);
        backend.Release.TrySetResult();
        await layer.DisposeAsync();
        Assert.True(backend.Confirmed);
        Assert.Equal(new InvalidationQueues(0, 0), memory.GetInvalidationQueues());
    }

    private sealed class HeldStampStore : IAtomicInvalidationStore
    {
        private readonly MethodCache.TestSupport.AtomicTestCache _inner = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Confirmed;
        public async Task WriteInvalidationStampAsync(string key, long timestamp, TimeSpan retention, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task;
            await _inner.WriteInvalidationStampAsync(key, timestamp, retention, cancellationToken);
            Confirmed = true;
        }
        public byte[]? Get(string key) => _inner.Get(key);
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => _inner.GetAsync(key, token);
        public void Set(string key, byte[] value, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions options) => _inner.Set(key, value, options);
        public Task SetAsync(string key, byte[] value, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions options, CancellationToken token = default) => _inner.SetAsync(key, value, options, token);
        public void Refresh(string key) => _inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);
        public void Remove(string key) => _inner.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);
    }

    private sealed class FailedPublication : IBackplane
    {
        private int _failed;
        public string InstanceId => "failed-publication";
        public Task PublishInvalidationAsync(string key, CancellationToken cancellationToken = default) => PublishTagInvalidationAsync(key, cancellationToken);
        public Task PublishTagInvalidationAsync(string tag, CancellationToken cancellationToken = default)
            => tag == "good" && Interlocked.Exchange(ref _failed, 1) == 0 ? Task.FromException(new InvalidOperationException("Publication failed")) : Task.CompletedTask;
        public Task SubscribeAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnsubscribeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
