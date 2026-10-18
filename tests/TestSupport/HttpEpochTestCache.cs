using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Caching.Distributed;

namespace MethodCache.TestSupport;

// Test-only native-capability model; each node shares this instance and its atomic gate.
// Network-provider atomicity must be established separately with the real Redis/SQL tests.
internal sealed class HttpEpochTestCache(IDistributedCache inner) : IAtomicInvalidationStore, IDistributedEpochStore
{
    private static readonly ConditionalWeakTable<IDistributedCache, HttpEpochTestCache> Wrappers = new();
    internal static IDistributedCache Wrap(IDistributedCache cache) => cache is HttpEpochTestCache ? cache : Wrappers.GetValue(cache, static c => new HttpEpochTestCache(c));
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _metadata = new(StringComparer.Ordinal);
    internal bool FailAdvance { get; set; }
    internal bool IncompatibleScope { get; set; }
    internal TimeSpan? EstablishedRetention { get; set; }
    internal bool FailRead { get; set; }
    internal bool FailDataRead { get; set; }
    internal bool FailStamp { get; set; }
    internal string? HoldStampAddress { get; set; }
    internal TaskCompletionSource StampStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource StampRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool HoldNextAdvance;
    internal bool HoldNextRead;
    internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ReadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Advances;
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int MetadataCount { get { lock (_gate) return _metadata.Count; } }
    internal void LoseMetadata() { lock (_gate) _metadata.Clear(); }
    public byte[]? Get(string key) { lock (_gate) return _metadata.TryGetValue(key, out var value) ? value.ToArray() : inner.Get(key); }
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return FailDataRead ? Task.FromException<byte[]?>(new IOException("Synthetic data read failure")) : Task.FromResult(Get(key));
    }
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => inner.Set(key, value, options);
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => inner.SetAsync(key, value, options, token);
    public void Remove(string key) { lock (_gate) _metadata.Remove(key); inner.Remove(key); }
    public Task RemoveAsync(string key, CancellationToken token = default) { token.ThrowIfCancellationRequested(); Remove(key); return Task.CompletedTask; }
    public void Refresh(string key) => inner.Refresh(key);
    public Task RefreshAsync(string key, CancellationToken token = default) => inner.RefreshAsync(key, token);
    public async Task WriteInvalidationStampAsync(string key, long stamp, TimeSpan retention, CancellationToken token = default)
    {
        if (FailStamp) throw new IOException("Synthetic stamp failure");
        if (key == HoldStampAddress)
        {
            HoldStampAddress = null; StampStarted.TrySetResult(); await StampRelease.Task.WaitAsync(token);
        }
        token.ThrowIfCancellationRequested();
        lock (_gate) { _metadata.TryGetValue(key, out var before); _metadata[key] = EpochMetadata.WithStamp(before, stamp, Guid.NewGuid()); }
    }
    public async ValueTask<CacheEpoch[]> EnsureEpochsAsync(IReadOnlyList<string> keys, CancellationToken token = default)
    {
        if (HoldNextRead) { HoldNextRead = false; ReadStarted.TrySetResult(); await ReadRelease.Task.WaitAsync(token); }
        token.ThrowIfCancellationRequested();
        if (FailRead) throw new IOException("Synthetic metadata read failure");
        lock (_gate)
        {
            var result = new CacheEpoch[keys.Count];
            for (var i = 0; i < keys.Count; i++)
            {
                _metadata.TryGetValue(keys[i], out var before);
                _metadata[keys[i]] = EpochMetadata.Ensure(before, Guid.NewGuid());
                _ = EpochMetadata.TryRead(_metadata[keys[i]], out result[i]);
            }
            return result;
        }
    }
    public async ValueTask<CacheEpoch> AdvanceEpochAsync(string key, CancellationToken token = default)
    {
        Interlocked.Increment(ref Advances);
        if (HoldNextAdvance) { HoldNextAdvance = false; Started.TrySetResult(); await Release.Task.WaitAsync(token); }
        if (FailAdvance) throw new IOException("Synthetic metadata advance failure");
        lock (_gate)
        {
            _metadata.TryGetValue(key, out var before);
            _metadata[key] = EpochMetadata.Advance(before, Guid.NewGuid());
            _ = EpochMetadata.TryRead(_metadata[key], out var result);
            return result;
        }
    }
    public ValueTask<TimeSpan> EnsureRetentionContractAsync(string key, TimeSpan requested, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return IncompatibleScope
            ? ValueTask.FromException<TimeSpan>(new IncompatibleDistributedScopeException("incompatible-scope-secret-canary"))
            : new(EstablishedRetention ?? requested);
    }
    internal static HttpEpochTestCache Create(TimeProvider? time = null) => new(new ClockCache(time ?? TimeProvider.System));
    private sealed class ClockCache(TimeProvider time) : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, (byte[] Bytes, DateTimeOffset Deadline)> _data = new(StringComparer.Ordinal);
        public byte[]? Get(string key) => _data.TryGetValue(key, out var item) && time.GetUtcNow() < item.Deadline ? item.Bytes.ToArray() : null;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) { token.ThrowIfCancellationRequested(); return Task.FromResult(Get(key)); }
        public void Set(string key, byte[] bytes, DistributedCacheEntryOptions options)
        { _data[key] = (bytes.ToArray(), options.AbsoluteExpiration ?? (options.AbsoluteExpirationRelativeToNow is { } ttl ? time.GetUtcNow() + ttl : DateTimeOffset.MaxValue)); }
        public Task SetAsync(string key, byte[] bytes, DistributedCacheEntryOptions options, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Set(key, bytes, options); return Task.CompletedTask; }
        public void Remove(string key) => _data.TryRemove(key, out _);
        public Task RemoveAsync(string key, CancellationToken token = default) { token.ThrowIfCancellationRequested(); Remove(key); return Task.CompletedTask; }
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
    }
}
