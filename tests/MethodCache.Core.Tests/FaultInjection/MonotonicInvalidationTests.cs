using System.Buffers.Binary;
using System.Collections.Concurrent;
using MethodCache.Core.Runtime.Distributed;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.FaultInjection;

public class MonotonicInvalidationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderInvalidationCompletesLast_ThirdNodeCannotReadInvalidatedEntry(bool clear)
    {
        var clock = new FakeTimeProvider();
        var cache = new ReorderedCache();
        var options = new DistributedCacheOptions { ClockSkewTolerance = TimeSpan.Zero, TagCheckInterval = TimeSpan.Zero, OperationTimeout = TimeSpan.FromMinutes(1) };
        using var older = new DistributedCacheLayer(cache, new JsonCacheSerializer(), options, timeProvider: clock);
        using var newer = new DistributedCacheLayer(cache, new JsonCacheSerializer(), options, timeProvider: clock);
        var oldInvalidation = clear ? older.ClearAsync() : older.InvalidateTagsAsync(["users"]);
        await cache.Started.Task;
        clock.Advance(TimeSpan.FromSeconds(1));
        await newer.WriteAsync("entry", "invalidated", ["users"], newer.Now, TimeSpan.FromMinutes(5));
        clock.Advance(TimeSpan.FromSeconds(1));
        if (clear) await newer.ClearAsync();
        else await newer.InvalidateTagsAsync(["users"]);
        cache.Release.TrySetResult();
        await oldInvalidation;
        using var third = new DistributedCacheLayer(cache, new JsonCacheSerializer(), options, timeProvider: clock);

        Assert.False((await third.TryGetAsync<string>("entry")).Found);
    }

    [Fact]
    public void CacheWithoutAtomicStampCapability_IsRejectedInsteadOfPretendingToBeSafe()
    {
        var clock = new FakeTimeProvider();
        Assert.Throws<ArgumentException>(() => new DistributedCacheLayer(new NonAtomicCache(), new JsonCacheSerializer(), new DistributedCacheOptions(), timeProvider: clock));
    }

    [Fact]
    public async Task OlderRemovalCompletesLast_DoesNotPermitAValueLoadedBetweenRemovals()
    {
        var clock = new FakeTimeProvider();
        var cache = new ReorderedCache(":r:");
        var options = new DistributedCacheOptions { ClockSkewTolerance = TimeSpan.Zero, TagCheckInterval = TimeSpan.Zero, OperationTimeout = TimeSpan.FromMinutes(1) };
        using var older = new DistributedCacheLayer(cache, new JsonCacheSerializer(), options, timeProvider: clock);
        using var newer = new DistributedCacheLayer(cache, new JsonCacheSerializer(), options, timeProvider: clock);
        var oldRemoval = older.RemoveAsync(["entry"]);
        await cache.Started.Task;
        clock.Advance(TimeSpan.FromSeconds(1));
        var loadedAt = newer.Now;
        clock.Advance(TimeSpan.FromSeconds(1));
        await newer.RemoveAsync(["entry"]);
        cache.Release.TrySetResult();
        await oldRemoval;

        await newer.WriteAsync("entry", "invalidated", null, loadedAt, TimeSpan.FromMinutes(5));
        using var third = new DistributedCacheLayer(cache, new JsonCacheSerializer(), options, timeProvider: clock);
        Assert.False((await third.TryGetAsync<string>("entry")).Found);
    }

    private class NonAtomicCache : IDistributedCache
    {
        protected readonly ConcurrentDictionary<string, byte[]> Data = new(StringComparer.Ordinal);
        public byte[]? Get(string key) => Data.TryGetValue(key, out var data) ? data : null;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => Data[key] = value;
        public virtual Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) { Set(key, value, options); return Task.CompletedTask; }
        public void Remove(string key) => Data.TryRemove(key, out _);
        public Task RemoveAsync(string key, CancellationToken token = default) { Remove(key); return Task.CompletedTask; }
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
    }

    private sealed class ReorderedCache(string stampKind = ":t:") : NonAtomicCache, IAtomicInvalidationStore
    {
        private int _stamps;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task HoldFirstStampAsync(string key)
        {
            if (key.Contains(stampKind, StringComparison.Ordinal) && Interlocked.Increment(ref _stamps) == 1)
            {
                Started.TrySetResult();
                await Release.Task;
            }
        }
        public override async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            await HoldFirstStampAsync(key);
            await base.SetAsync(key, value, options, token);
        }
        public async Task WriteInvalidationStampAsync(string key, long timestamp, TimeSpan retention, CancellationToken cancellationToken = default)
        {
            await HoldFirstStampAsync(key);
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, timestamp);
            Data.AddOrUpdate(key, bytes, (_, previous) => BinaryPrimitives.ReadInt64LittleEndian(previous) < timestamp ? bytes : previous);
        }
    }
}
