using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Buffers.Binary;
using MethodCache.Core.Runtime.Distributed;
using MethodCache.Core.Runtime.Memory;
using MethodCache.Core.Telemetry;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Options;

namespace MethodCache.Core.Runtime.Http;

internal sealed record HttpStorageInfo(string Id, string UrlTarget, string Client, int Format, long Cost,
    string[] Tags, DateTimeOffset CreatedAt, DateTimeOffset Deadline, bool Selector);

// Protocol-neutral storage: the add-on owns its HTTP codec and freshness rules. Core owns residency,
// byte accounting, permanent generation metadata, bounded observations and invalidation recovery.
internal sealed class HttpResponseStore : IDisposable, IAsyncDisposable
{
    internal const int GenerationSlots = 4096;
    internal const int ObservationLimit = 1024;
    private readonly MemoryCacheEngine? _local;
    private readonly MemoryCacheEngine _observations;
    private readonly TimeProvider _time;
    private readonly DistributedCacheLayer? _distributed;
    private readonly CoreTelemetry _telemetry;
    private readonly LocalEpoch?[]? _slots;
    private readonly ConcurrentDictionary<string, LocalEpoch>? _tagEpochs;
    private LocalEpoch _clear = LocalEpoch.Fresh();
    private readonly ConcurrentDictionary<int, PendingEpoch> _pending = new();
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private readonly object _timerGate = new();
    private ITimer? _retry;
    private long _ticket;
    private int _disposed;
    internal byte Format { get; }
    internal long MaximumBytes { get; }
    internal bool Distributed => _distributed != null;
    internal int PendingRecoveries => _pending.Count(pair => pair.Value.Queued);
    private sealed record LocalEpoch(CacheEpoch Value)
    {
        internal static LocalEpoch Fresh() => new(new CacheEpoch(Guid.NewGuid(), 0));
    }
    private sealed record PendingEpoch(long Ticket, bool Queued);

    internal HttpResponseStore(IOptions<MethodCacheMemoryOptions> options, CoreTelemetry telemetry,
        TimeProvider? time = null, DistributedCacheLayer? distributed = null, byte format = 2)
    {
        ArgumentOutOfRangeException.ThrowIfZero(format);
        Format = format;
        _time = time ?? TimeProvider.System;
        _distributed = distributed;
        _telemetry = telemetry;
        MaximumBytes = options.Value.MaxHttpBytes;
        var clock = Clock(_time);
        _observations = new MemoryCacheEngine(ObservationLimit, EngineEvictionMode.Fifo, clock, ObservationLimit)
        { ForegroundMaintenance = true };
        if (distributed == null)
        {
            _local = new MemoryCacheEngine(MaximumBytes, EngineEvictionMode.TinyLfu, clock, 128);
            _slots = new LocalEpoch?[GenerationSlots];
            _tagEpochs = new(StringComparer.Ordinal);
        }
        else
        {
            if (!distributed.SupportsHttpEpochs)
                throw new NotSupportedException("HTTP L2 caching requires MethodCache's native Redis or SQL Server transport.");
            distributed.AttachHttp(InvalidateLocalTag, ClearLocal);
            distributed.ObserveHttpRecovery(() => PendingRecoveries);
        }
        telemetry.ObserveHttp(Observe);
    }

    internal string[] EpochAddresses(int slot, IReadOnlyList<string> tags)
    {
        var keys = new string[tags.Count + 2];
        keys[0] = _distributed?.HttpClearAddress ?? "\0http-clear";
        keys[1] = _distributed?.HttpSlotAddress(slot) ?? "\0http-slot:" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < tags.Count; i++) keys[i + 2] = _distributed?.HttpTagAddress(tags[i]) ?? tags[i];
        return keys;
    }

    internal async ValueTask<string?> CaptureAsync(string urlTarget, string[] tags, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        token.ThrowIfCancellationRequested();
        var slot = Slot(urlTarget);
        // A locally unconfirmed mutation never selects a previously stored response on this node.
        if (!_pending.IsEmpty) return null;
        var addresses = EpochAddresses(slot, tags);
        CacheEpoch[]? epochs;
        if (_distributed != null)
            epochs = await _distributed.CaptureHttpEpochsAsync(addresses, token).ConfigureAwait(false);
        else
        {
            var current = Volatile.Read(ref _slots![slot]);
            if (current == null)
            {
                var fresh = LocalEpoch.Fresh();
                current = Interlocked.CompareExchange(ref _slots[slot], fresh, null) ?? fresh;
            }
            epochs = new CacheEpoch[addresses.Length];
            epochs[0] = Volatile.Read(ref _clear).Value;
            epochs[1] = current.Value;
            for (var i = 0; i < tags.Length; i++) epochs[i + 2] = _tagEpochs!.GetOrAdd(tags[i], static _ => LocalEpoch.Fresh()).Value;
        }
        if (epochs == null || epochs.Length != addresses.Length) return null;
        return VectorIdentity(addresses, epochs);
    }

    private static string VectorIdentity(string[] addresses, CacheEpoch[] epochs)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> frame = stackalloc byte[24];
        for (var i = 0; i < epochs.Length; i++)
        {
            // Include component addresses as well as values: two configured tag sets cannot share a vector.
            var name = LosslessUtf16.GetBytes(addresses[i]);
            BinaryPrimitives.WriteInt32LittleEndian(frame, name.Length);
            hash.AppendData(frame[..4]); hash.AppendData(name);
            epochs[i].Initialization.TryWriteBytes(frame);
            BinaryPrimitives.WriteUInt64BigEndian(frame[16..], epochs[i].Revision);
            hash.AppendData(frame);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal object? ReadLocal(string key)
        => _local != null && _local.TryGet(key, _local.Now, out var node) ? node.BoxedValue : null;

    internal bool StoreLocal(string key, object payload, HttpStorageInfo info)
    {
        if (_local == null || info.Cost <= 0 || info.Cost > MaximumBytes || info.Deadline <= _time.GetUtcNow()) return false;
        var now = _local.Now;
        var deadline = Deadline(now, info.Deadline - _time.GetUtcNow());
        _local.Add(new ValueNode<object>(key, payload)
        {
            Weight = info.Cost,
            Tags = IndexTags(info),
            Policy = info,
            CreatedAt = now,
            LogicalExpiresAt = deadline,
            PhysicalExpiresAt = deadline,
            AbsoluteExpiresAt = deadline
        });
        return true;
    }

    internal void ObserveRepresentation(HttpStorageInfo info)
    {
        if (!Distributed || info.Selector || info.Deadline <= _time.GetUtcNow()) return;
        var now = _observations.Now;
        var deadline = Deadline(now, info.Deadline - _time.GetUtcNow());
        _observations.Add(new ValueNode<HttpStorageInfo>(info.Id, info)
        { Tags = IndexTags(info), LogicalExpiresAt = deadline, PhysicalExpiresAt = deadline, AbsoluteExpiresAt = deadline });
    }

    internal ValueTask<byte[]?> ReadBytesAsync(string key, CancellationToken token) => _distributed!.ReadHttpAsync(key, Format, token);
    internal ValueTask<bool> WriteBytesAsync(string key, byte[] bytes, DateTimeOffset deadline, CancellationToken token)
        => _distributed!.WriteHttpAsync(key, Format, bytes, deadline, token);
    internal async ValueTask RemoveAsync(string key, CancellationToken token)
    {
        if (_local != null) _local.Remove(key);
        else await _distributed!.RemoveHttpAsync(key, Format, token).ConfigureAwait(false);
    }

    internal void ForgetUrl(string target)
    {
        _observations.RemoveByTag(UrlTag(target));
    }

    internal ValueTask InvalidateUrlsAsync(IEnumerable<string> targets, CancellationToken caller)
    {
        var distinct = targets.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var target in distinct)
        {
            _local?.RemoveByTag(UrlTag(target));
            ForgetUrl(target);
        }
        var slots = distinct.Select(Slot).Distinct().ToArray();
        _telemetry.Invalidations("key", "local", distinct.Length);
        if (_distributed == null)
        {
            foreach (var slot in slots) Volatile.Write(ref _slots![slot], LocalEpoch.Fresh());
            return ValueTask.CompletedTask;
        }
        var operations = slots.Select(slot =>
        {
            var pending = new PendingEpoch(Interlocked.Increment(ref _ticket), false);
            _pending[slot] = pending;
            return AdvanceAsync(slot, pending);
        }).ToArray(); // start every target before the caller can stop waiting
        var owned = ConfirmAsync(operations);
        _ = owned.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return new ValueTask(owned.WaitAsync(caller));
    }

    private static async Task ConfirmAsync(Task<bool>[] operations)
    {
        if ((await Task.WhenAll(operations).ConfigureAwait(false)).Any(static confirmed => !confirmed))
            throw new InvalidOperationException("The HTTP origin mutation may have succeeded, but its cache invalidation could not be confirmed. The cache owns recovery; do not retry the origin mutation automatically.");
    }

    private async Task<bool> AdvanceAsync(int slot, PendingEpoch pending)
    {
        bool confirmed;
        try { confirmed = await _distributed!.AdvanceHttpSlotAsync(slot).ConfigureAwait(false); }
        catch { Queue(slot, pending); throw; }
        if (confirmed)
        {
            _pending.TryRemove(new KeyValuePair<int, PendingEpoch>(slot, pending));
            _telemetry.Invalidations("key", "distributed", 1);
        }
        else Queue(slot, pending);
        return confirmed;
    }

    private void Queue(int slot, PendingEpoch pending)
    {
        if (_pending.TryUpdate(slot, pending with { Queued = true }, pending) && !pending.Queued)
            _telemetry.Invalidations("key", "queued", 1);
        lock (_timerGate)
        {
            if (_retry == null && Volatile.Read(ref _disposed) == 0)
                _retry = _time.CreateTimer(static state => _ = ((HttpResponseStore)state!).FlushRecoveryAsync(), this,
                    _distributed!.HttpRecoveryInterval, _distributed.HttpRecoveryInterval);
        }
    }

    internal async Task FlushRecoveryAsync()
    {
        if (!_recoveryGate.Wait(0)) return;
        try
        {
            foreach (var pair in _pending.ToArray())
            {
                if (!pair.Value.Queued) continue;
                try { _ = await AdvanceAsync(pair.Key, pair.Value).ConfigureAwait(false); }
                catch { /* Its bounded transport operation reports the type and retains this target for recovery. */ }
            }
        }
        finally { _recoveryGate.Release(); }
    }

    internal void InvalidateLocalTag(string tag)
    {
        if (_tagEpochs?.ContainsKey(tag) == true) _tagEpochs.AddOrUpdate(tag, static _ => LocalEpoch.Fresh(), static (_, _) => LocalEpoch.Fresh());
        _local?.RemoveByTag(ApplicationTag(tag));
        _observations.RemoveByTag(ApplicationTag(tag));
    }

    internal void ClearLocal()
    {
        Volatile.Write(ref _clear, LocalEpoch.Fresh());
        _local?.Clear(); _observations.Clear();
    }

    internal CacheObservation Observe()
    {
        if (Volatile.Read(ref _disposed) != 0) return default;
        _local?.Maintain(); _observations.Maintain();
        var localCount = _local?.Map.Count(pair => pair.Value.Policy is HttpStorageInfo { Selector: false } && !pair.Value.IsPhysicallyExpired(_local.Now)) ?? 0;
        return new CacheObservation(0, localCount, _local?.TotalWeight ?? 0, _observations.Count, 0);
    }

    internal IReadOnlyList<HttpStorageInfo> List()
    {
        _local?.Maintain(); _observations.Maintain();
        var result = new List<HttpStorageInfo>();
        if (_local != null)
        {
            foreach (var node in _local.Map.Values)
                if (!node.IsPhysicallyExpired(_local.Now) && node.Policy is HttpStorageInfo { Selector: false } info) result.Add(info);
        }
        else
            foreach (var node in _observations.Map.Values)
                if (!node.IsPhysicallyExpired(_observations.Now) && node.BoxedValue is HttpStorageInfo info) result.Add(info);
        return result;
    }

    internal IReadOnlyList<CacheEntryInfo> DescribeEntries(string? prefix, string? tag, int limit)
        => List().Where(info => (prefix == null || info.Id.StartsWith(prefix, StringComparison.Ordinal)) &&
                (tag == null || info.Tags.Contains(tag, StringComparer.Ordinal)))
            .OrderBy(info => info.Id, StringComparer.Ordinal).Take(limit).Select(Describe).ToArray();

    internal CacheEntryInfo Describe(HttpStorageInfo info)
    {
        var now = _time.GetUtcNow();
        return new CacheEntryInfo(info.Id, "HTTP response", now > info.CreatedAt ? now - info.CreatedAt : TimeSpan.Zero,
            info.Deadline > now ? info.Deadline - now : TimeSpan.Zero, Array.AsReadOnly(info.Tags.ToArray()), 0)
        {
            Kind = CacheEntryKind.Http,
            Storage = Distributed ? CacheEntryStorage.ObservedDistributed : CacheEntryStorage.LocalMemory,
            Client = info.Client,
            Format = info.Format,
            SizeBytes = info.Cost,
            Description = Distributed ? "Historical local observation; not verified L2 presence or a global inventory." : null
        };
    }

    internal async ValueTask InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken token)
    {
        foreach (var tag in tags) InvalidateLocalTag(tag);
        _telemetry.Invalidations("tag", "local", tags.Distinct(StringComparer.Ordinal).LongCount());
        if (_distributed != null) _ = await _distributed.InvalidateTargetsAsync(tags.Distinct(StringComparer.Ordinal).ToArray(), tags: true).WaitAsync(token).ConfigureAwait(false);
    }

    internal async ValueTask ClearAsync(CancellationToken token)
    {
        ClearLocal();
        _telemetry.Invalidations("tag", "local", 1);
        if (_distributed != null) await _distributed.ClearAsync().WaitAsync(token).ConfigureAwait(false);
    }
    internal ValueTask<bool?> CheckDistributedAsync(CancellationToken token)
        => _distributed == null ? new ValueTask<bool?>((bool?)null) : CheckAsync(token);
    private async ValueTask<bool?> CheckAsync(CancellationToken token)
        => await _distributed!.CheckAvailableAsync(token).ConfigureAwait(false);
    internal InvalidationQueues RecoveryQueues => _distributed?.GetInvalidationQueues() ?? new(0, 0);

    internal HttpStorageInfo? Find(string id) => List().FirstOrDefault(info => info.Id == id);
    internal async ValueTask<bool> RemoveObservationAsync(string id, CancellationToken token)
    {
        var known = Find(id);
        if (known == null) return false;
        await InvalidateUrlsAsync([known.UrlTarget], token).ConfigureAwait(false);
        return true;
    }

    internal static string Identity(string representationKey) => "http:" + Convert.ToHexString(SHA256.HashData(LosslessUtf16.GetBytes(representationKey)));
    private static string UrlTag(string target) => "\0http-url:" + target;
    private static string ApplicationTag(string tag) => "\0http-tag:" + Convert.ToBase64String(LosslessUtf16.GetBytes(tag));
    private static string[] IndexTags(HttpStorageInfo info) => info.Tags.Select(ApplicationTag).Append(UrlTag(info.UrlTarget)).ToArray();
    internal static int Slot(string target)
    {
        var hash = 2166136261u;
        foreach (var character in target) hash = unchecked((hash ^ character) * 16777619u);
        return (int)(hash & (GenerationSlots - 1));
    }
    private static long Deadline(long now, TimeSpan remaining)
        => remaining.Ticks > (long.MaxValue - now) / 100 ? long.MaxValue : now + Math.Max(1, remaining.Ticks * 100);
    private static Func<long> Clock(TimeProvider time)
    {
        if (ReferenceEquals(time, TimeProvider.System)) return static () => Environment.TickCount64 * 1_000_000;
        var origin = time.GetTimestamp(); var scale = 1_000_000_000d / time.TimestampFrequency;
        return () => (long)((time.GetTimestamp() - origin) * scale);
    }
    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_timerGate) _retry?.Dispose();
        _distributed?.ObserveHttpRecovery(null);
        _local?.Clear(); _observations.Clear();
    }
    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try { if (Distributed) await FlushRecoveryAsync().ConfigureAwait(false); }
        finally { Dispose(); }
    }
}
