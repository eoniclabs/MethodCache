using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using MethodCache.Extensibility;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MethodCache.Redis;

/// <summary>
/// MethodCache's own Redis cache. Not disposable on purpose: disposing <see cref="RedisCache"/> closes its
/// connection, which may be the app's; <see cref="RedisConnection"/> disposes the connection when MethodCache owns it.
/// </summary>
internal sealed class RedisDistributedCache(RedisConnection connection) : IBufferDistributedCache, IAtomicInvalidationStore
{
    // Compare signed 64-bit little-endian bytes directly. Lua numbers cannot represent every long exactly.
    private const string StampScript = """
        local previous = redis.call('HGET', KEYS[1], 'data')
        local ttl = redis.call('PTTL', KEYS[1])
        local incoming = ARGV[1]
        local replace = not previous or string.len(previous) < 8
        if not replace then
            for i = 8, 1, -1 do
                local old = string.byte(previous, i)
                local new = string.byte(incoming, i)
                if i == 8 then old = (old + 128) % 256; new = (new + 128) % 256 end
                if old ~= new then replace = new > old; break end
            end
        end
        redis.call('HSET', KEYS[1], 'data', replace and incoming or string.sub(previous, 1, 8))
        redis.call('HSET', KEYS[1], 'absexp', '-1', 'sldexp', '-1')
        if ttl ~= -1 and ttl < tonumber(ARGV[2]) then redis.call('PEXPIRE', KEYS[1], ARGV[2]) end
        return 1
        """;

    private readonly IBufferDistributedCache _inner = new RedisCache(Options.Create(new RedisCacheOptions
    {
        ConnectionMultiplexerFactory = connection.GetAsync,
    }));

    public byte[]? Get(string key) => _inner.Get(GetPhysicalKey(key));
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => _inner.GetAsync(GetPhysicalKey(key), token);
    public bool TryGet(string key, IBufferWriter<byte> destination) => _inner.TryGet(GetPhysicalKey(key), destination);
    public ValueTask<bool> TryGetAsync(string key, IBufferWriter<byte> destination, CancellationToken token = default) => _inner.TryGetAsync(GetPhysicalKey(key), destination, token);
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _inner.Set(GetPhysicalKey(key), value, options);
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => _inner.SetAsync(GetPhysicalKey(key), value, options, token);
    public void Set(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options) => _inner.Set(GetPhysicalKey(key), value, options);
    public ValueTask SetAsync(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options, CancellationToken token = default) => _inner.SetAsync(GetPhysicalKey(key), value, options, token);
    public void Refresh(string key) => _inner.Refresh(GetPhysicalKey(key));
    public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(GetPhysicalKey(key), token);
    public void Remove(string key) => _inner.Remove(GetPhysicalKey(key));
    public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(GetPhysicalKey(key), token);

    // Keep ordinary keys and their cleanup prefixes unchanged. Escape both the escape character and every
    // unpaired surrogate before UTF-8 encoding, so a literal escape cannot impersonate another logical key.
    internal static string GetPhysicalKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var first = -1;
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (char.IsHighSurrogate(c) && i + 1 < key.Length && char.IsLowSurrogate(key[i + 1])) { i++; continue; }
            if (c == '\\' || char.IsSurrogate(c)) { first = i; break; }
        }
        if (first < 0) return key;
        var physical = new StringBuilder(key.Length + 16).Append(key, 0, first);
        for (var i = first; i < key.Length; i++)
        {
            var c = key[i];
            if (c == '\\') physical.Append("\\\\");
            else if (char.IsHighSurrogate(c) && i + 1 < key.Length && char.IsLowSurrogate(key[i + 1]))
                physical.Append(c).Append(key[++i]);
            else if (char.IsSurrogate(c))
                physical.Append("\\u").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            else physical.Append(c);
        }
        return physical.ToString();
    }

    public async Task WriteInvalidationStampAsync(string key, long timestamp, TimeSpan retention, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (retention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
        cancellationToken.ThrowIfCancellationRequested();
        var stamp = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(stamp, timestamp);
        var multiplexer = await connection.GetAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        await multiplexer.GetDatabase().ScriptEvaluateAsync(StampScript, new RedisKey[] { GetPhysicalKey(key) },
            new RedisValue[] { stamp, (long)Math.Ceiling(retention.TotalMilliseconds) }).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
