using System.Buffers;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;

namespace MethodCache.Redis;

/// <summary>
/// MethodCache's own Redis cache. Not disposable on purpose: disposing <see cref="RedisCache"/> closes its
/// connection, which may be the app's; <see cref="RedisConnection"/> disposes the connection when MethodCache owns it.
/// </summary>
internal sealed class RedisDistributedCache(RedisConnection connection) : IBufferDistributedCache
{
    private readonly IBufferDistributedCache _inner = new RedisCache(Options.Create(new RedisCacheOptions
    {
        ConnectionMultiplexerFactory = connection.GetAsync,
    }));

    public byte[]? Get(string key) => _inner.Get(key);
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => _inner.GetAsync(key, token);
    public bool TryGet(string key, IBufferWriter<byte> destination) => _inner.TryGet(key, destination);
    public ValueTask<bool> TryGetAsync(string key, IBufferWriter<byte> destination, CancellationToken token = default) => _inner.TryGetAsync(key, destination, token);
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _inner.Set(key, value, options);
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => _inner.SetAsync(key, value, options, token);
    public void Set(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options) => _inner.Set(key, value, options);
    public ValueTask SetAsync(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options, CancellationToken token = default) => _inner.SetAsync(key, value, options, token);
    public void Refresh(string key) => _inner.Refresh(key);
    public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);
    public void Remove(string key) => _inner.Remove(key);
    public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);
}
