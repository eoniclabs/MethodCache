using System.Buffers.Binary;
using MethodCache.Extensibility;
using Microsoft.Extensions.Caching.Distributed;

namespace MethodCache.Core.Tests.FaultInjection;

// Ordinal keys and expiry on the registered fake clock. Holds selected writes to test late publication.
internal sealed class ExpiringAtomicCache(TimeProvider clock) : IAtomicInvalidationStore
{
    private readonly Dictionary<string, (byte[] Value, DateTimeOffset Expires)> _data = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    public Func<string, Task>? BeforeWrite { get; set; }
    public DateTimeOffset? ExpirationFor(string key)
    { lock (_sync) return _data.TryGetValue(key, out var entry) ? entry.Expires : null; }

    public byte[]? Get(string key)
    {
        lock (_sync) return _data.TryGetValue(key, out var entry) && entry.Expires > clock.GetUtcNow() ? entry.Value.ToArray() : null;
    }
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        lock (_sync) _data[key] = (value.ToArray(), options.AbsoluteExpiration ??
            clock.GetUtcNow() + (options.AbsoluteExpirationRelativeToNow ?? TimeSpan.FromDays(1)));
    }
    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        if (BeforeWrite is { } hold) await hold(key);
        Set(key, value, options);
    }
    public void Remove(string key) { lock (_sync) _data.Remove(key); }
    public Task RemoveAsync(string key, CancellationToken token = default) { Remove(key); return Task.CompletedTask; }
    public void Refresh(string key) { }
    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
    public Task WriteInvalidationStampAsync(string key, long timestamp, TimeSpan retention, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var expiry = clock.GetUtcNow() + retention;
            var previous = Get(key);
            if (previous != null)
            {
                timestamp = Math.Max(timestamp, BinaryPrimitives.ReadInt64LittleEndian(previous));
                if (_data[key].Expires > expiry) expiry = _data[key].Expires;
            }
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, timestamp);
            _data[key] = (bytes, expiry);
        }
        return Task.CompletedTask;
    }
}
