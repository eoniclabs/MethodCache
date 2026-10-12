using System.Buffers.Binary;
using MethodCache.Extensibility;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;

namespace MethodCache.TestSupport;

// Test-only, in-process backend. Every node shares this instance, so one semaphore is genuinely atomic for it.
// This must not be used to claim cross-process atomicity for a network cache.
internal sealed class AtomicTestCache : IAtomicInvalidationStore
{
    private readonly IDistributedCache _inner = new MemoryDistributedCache(
        Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
    private readonly SemaphoreSlim _stamps = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _expirations = new(StringComparer.Ordinal);

    public byte[]? Get(string key) => _inner.Get(key);
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => _inner.GetAsync(key, token);
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _inner.Set(key, value, options);
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => _inner.SetAsync(key, value, options, token);
    public void Refresh(string key) => _inner.Refresh(key);
    public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);
    public void Remove(string key) => _inner.Remove(key);
    public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);

    public async Task WriteInvalidationStampAsync(string key, long timestamp, TimeSpan retention, CancellationToken cancellationToken = default)
    {
        await _stamps.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = await _inner.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (previous is { Length: >= 8 }) timestamp = Math.Max(timestamp, BinaryPrimitives.ReadInt64LittleEndian(previous));
            var stamp = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(stamp, timestamp);
            var expires = DateTimeOffset.UtcNow + retention;
            if (previous != null && _expirations.TryGetValue(key, out var current) && current > expires) expires = current;
            _expirations[key] = expires;
            await _inner.SetAsync(key, stamp, new DistributedCacheEntryOptions { AbsoluteExpiration = expires }, cancellationToken).ConfigureAwait(false);
        }
        finally { _stamps.Release(); }
    }
}
