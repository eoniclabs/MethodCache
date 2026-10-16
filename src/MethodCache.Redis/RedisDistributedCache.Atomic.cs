using System.Buffers.Binary;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using StackExchange.Redis;

namespace MethodCache.Redis;

internal sealed partial class RedisDistributedCache
{
    // Each script touches exactly one key, including on Redis Cluster. Batched ensures pipeline those operations.
    private const string EpochScript = """
        local current = redis.call('HMGET', KEYS[1], 'data', 'absexp', 'sldexp')
        local previous = current[1]
        if previous and string.len(previous) ~= 8 and (string.len(previous) < 33 or string.byte(previous, 9) ~= 1) then
            return redis.error_reply('MethodCache epoch schema mismatch')
        end
        -- Reading an initialized, permanent epoch changes nothing, so it writes nothing.
        if ARGV[2] ~= 'advance' and previous and string.len(previous) ~= 8
            and current[2] == '-1' and current[3] == '-1' and redis.call('TTL', KEYS[1]) == -1 then
            return previous
        end
        local data = previous
        if not data or string.len(data) == 8 then
            data = (data or ARGV[3]) .. string.char(1) .. ARGV[1] .. string.rep(string.char(0), 8)
        end
        if ARGV[2] == 'advance' then
            local revision = {string.byte(data, 26, 33)}
            local carry = true
            for i = 8, 1, -1 do
                if revision[i] < 255 then revision[i] = revision[i] + 1; carry = false; break end
                revision[i] = 0
            end
            local id = carry and ARGV[1] or string.sub(data, 10, 25)
            data = string.sub(data, 1, 9) .. id .. string.char(unpack(revision)) .. string.sub(data, 34)
        end
        redis.call('HSET', KEYS[1], 'data', data, 'absexp', '-1', 'sldexp', '-1')
        redis.call('PERSIST', KEYS[1])
        return data
        """;

    private const string RetentionScript = """
        local previous = redis.call('HGET', KEYS[1], 'data')
        if not previous then
            previous = ARGV[1]
            redis.call('HSET', KEYS[1], 'data', previous, 'absexp', '-1', 'sldexp', '-1')
        end
        redis.call('PERSIST', KEYS[1])
        return previous
        """;

    public async ValueTask<DistributedRead> ReadEntryAsync(string valueKey, string removalKey, CancellationToken cancellationToken = default)
    {
        var connection = await _connection.GetAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var batch = connection.GetDatabase().CreateBatch();
        var value = batch.HashGetAsync(GetPhysicalKey(valueKey), "data");
        var removal = batch.HashGetAsync(GetPhysicalKey(removalKey), "data");
        batch.Execute();
        await Task.WhenAll(value, removal).WaitAsync(cancellationToken).ConfigureAwait(false);
        return new DistributedRead((byte[]?)await value.ConfigureAwait(false), (byte[]?)await removal.ConfigureAwait(false));
    }

    public async ValueTask<CacheEpoch[]> EnsureEpochsAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();
        if (keys.Count == 0) return [];
        foreach (var key in keys) ArgumentNullException.ThrowIfNull(key);
        if (keys.Count > 128) throw new ArgumentOutOfRangeException(nameof(keys), "A metadata batch may contain at most 128 keys.");
        var connection = await _connection.GetAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var batch = connection.GetDatabase().CreateBatch();
        var pending = new Task<RedisResult>[keys.Count];
        for (var i = 0; i < keys.Count; i++)
            pending[i] = batch.ScriptEvaluateAsync(EpochScript, new RedisKey[] { GetPhysicalKey(keys[i]) }, EpochArguments(advance: false));
        batch.Execute();
        var values = await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
        var result = new CacheEpoch[values.Length];
        for (var i = 0; i < values.Length; i++) result[i] = ReadEpoch(values[i]);
        return result;
    }

    public async ValueTask<CacheEpoch> AdvanceEpochAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        var connection = await _connection.GetAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var value = await connection.GetDatabase().ScriptEvaluateAsync(EpochScript, new RedisKey[] { GetPhysicalKey(key) }, EpochArguments(advance: true))
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return ReadEpoch(value);
    }

    public async ValueTask<TimeSpan> EnsureRetentionContractAsync(string key, TimeSpan requested, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (requested <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requested));
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, requested.Ticks);
        var connection = await _connection.GetAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var value = await connection.GetDatabase().ScriptEvaluateAsync(RetentionScript, new RedisKey[] { GetPhysicalKey(key) }, new RedisValue[] { bytes })
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        var stored = (byte[]?)value;
        if (stored is not { Length: 8 }) throw new IncompatibleDistributedScopeException("The distributed retention contract has an incompatible schema.");
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(stored);
        if (ticks <= 0 || ticks > requested.Ticks)
            throw new IncompatibleDistributedScopeException("TagRetention cannot be shorter than this scope's established metadata retention. Use a new KeyPrefix for a new contract.");
        return TimeSpan.FromTicks(ticks);
    }

    private static RedisValue[] EpochArguments(bool advance)
    {
        var emptyStamp = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(emptyStamp, long.MinValue);
        return [Guid.NewGuid().ToByteArray(), advance ? "advance" : "ensure", emptyStamp];
    }

    private static CacheEpoch ReadEpoch(RedisResult result)
    {
        var data = (byte[]?)result;
        if (data == null || !EpochMetadata.TryRead(data, out var epoch))
            throw new IncompatibleDistributedScopeException("The distributed epoch metadata has an incompatible schema.");
        return epoch;
    }
}
