using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
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
    private readonly string?[]? _localSlotTags;
    private readonly ConditionalWeakTable<string[], string[]> _applicationIndexes = new();
    private readonly ConcurrentDictionary<string, LocalEpoch>? _tagEpochs;
    private LocalEpoch _clear = LocalEpoch.Fresh(initial: true);
    private readonly ConcurrentDictionary<int, PendingEpoch> _pending = new();
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private readonly object _timerGate = new();
    private ITimer? _retry;
    private long _ticket;
    private int _disposed;
    private int _advancesInFlight;
    private readonly TaskCompletionSource _advancesDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _shutdown;
    internal byte Format { get; }
    internal long MaximumBytes { get; }
    internal bool Distributed => _distributed != null;
    internal int PendingRecoveries => _pending.Count(pair => pair.Value.Queued);
    private sealed class LocalEpoch(CacheEpoch value, bool initial)
    {
        internal CacheEpoch Value { get; } = value;
        internal bool Initial { get; } = initial;
        internal LocalVector? LastVector;
        internal static LocalEpoch Fresh(bool initial = false) => new(new CacheEpoch(Guid.NewGuid(), 0), initial);
    }
    private sealed record LocalVector(LocalEpoch Clear, string[] Tags, LocalEpoch[] TagEpochs, bool AllowInitialNamespace, string Identity);
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
            _local = new MemoryCacheEngine(MaximumBytes, EngineEvictionMode.TinyLfu, clock, 128, useRuntimeStringComparer: true);
            _slots = new LocalEpoch?[GenerationSlots];
            _localSlotTags = new string?[GenerationSlots];
            _tagEpochs = new(StringComparer.Ordinal);
        }
        else
        {
            if (!distributed.SupportsHttpEpochs)
                throw new NotSupportedException("HTTP L2 caching requires MethodCache's native Redis or SQL Server transport.");
            distributed.AttachHttp(InvalidateLocalTag, ClearLocal, DisposeAsync);
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

    internal ValueTask<string?> CaptureAsync(string urlTarget, string[] tags, CancellationToken token)
        => CaptureSlotAsync(Slot(urlTarget), tags, token);

    internal ValueTask<string?> CaptureSlotAsync(int slot, string[] tags, CancellationToken token, bool allowInitialNamespace = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        token.ThrowIfCancellationRequested();
        if (_distributed == null) return new(CaptureLocal(slot, tags, allowInitialNamespace));
        // Native mode always reads fresh shared epochs; it never uses a local vector or epoch copy.
        if (!_pending.IsEmpty) return new((string?)null);
        return CaptureDistributedAsync(slot, tags, token);
    }

    private async ValueTask<string?> CaptureDistributedAsync(int slot, string[] tags, CancellationToken token)
    {
        var addresses = EpochAddresses(slot, tags);
        var epochs = await _distributed!.CaptureHttpEpochsAsync(addresses, token).ConfigureAwait(false);
        return epochs == null || epochs.Length != addresses.Length ? null : VectorIdentity(addresses, epochs);
    }

    private string CaptureLocal(int slot, string[] tags, bool allowInitialNamespace)
    {
        var current = Volatile.Read(ref _slots![slot]);
        if (current == null)
        {
            var fresh = LocalEpoch.Fresh(initial: true);
            current = Interlocked.CompareExchange(ref _slots[slot], fresh, null) ?? fresh;
        }
        var clear = Volatile.Read(ref _clear);
        var previous = Volatile.Read(ref current.LastVector);
        if (previous != null && ReferenceEquals(previous.Clear, clear) && ReferenceEquals(previous.Tags, tags) && previous.AllowInitialNamespace == allowInitialNamespace)
        {
            var unchanged = true;
            for (var i = 0; i < tags.Length; i++)
                if (!_tagEpochs!.TryGetValue(tags[i], out var epoch) || !ReferenceEquals(epoch, previous.TagEpochs[i]))
                { unchanged = false; break; }
            if (unchanged) return previous.Identity;
        }
        var epochs = new CacheEpoch[tags.Length + 2];
        var tagEpochs = new LocalEpoch[tags.Length];
        epochs[0] = clear.Value; epochs[1] = current.Value;
        for (var i = 0; i < tags.Length; i++)
        {
            tagEpochs[i] = _tagEpochs!.GetOrAdd(tags[i], static _ => LocalEpoch.Fresh(initial: true));
            epochs[i + 2] = tagEpochs[i].Value;
        }
        // A canonical default client already identifies its one implicit client tag in the URL key.
        // Its initial local namespace can remain empty; no invalidation can return these cells to Initial.
        var identity = allowInitialNamespace && clear.Initial && current.Initial && tagEpochs.All(static epoch => epoch.Initial)
            ? string.Empty : VectorIdentity(EpochAddresses(slot, tags), epochs);
        // One immutable cached vector per permanent slot, at most 4096. Alternating tag sets recompute
        // the same deterministic identity; they never invent a new generation or affect cache selection.
        Volatile.Write(ref current.LastVector, new LocalVector(clear, tags, tagEpochs, allowInitialNamespace, identity));
        return identity;
    }

    internal static string VectorIdentity(string[] addresses, CacheEpoch[] epochs)
    {
        // Each component is framed as its address length, address code units, init-id and BE revision.
        var length = 0;
        for (var i = 0; i < epochs.Length; i++) length = checked(length + sizeof(int) + addresses[i].Length * sizeof(char) + 24);
        byte[]? rented = null;
        Span<byte> frames = length <= 1024 ? stackalloc byte[1024] : rented = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var position = 0;
            for (var i = 0; i < epochs.Length; i++)
            {
                // Include component addresses as well as values: two configured tag sets cannot share a vector.
                var name = addresses[i].Length * sizeof(char);
                BinaryPrimitives.WriteInt32LittleEndian(frames[position..], name);
                LosslessUtf16.Write(addresses[i], frames[(position + sizeof(int))..]);
                position += sizeof(int) + name;
                epochs[i].Initialization.TryWriteBytes(frames[position..]);
                BinaryPrimitives.WriteUInt64BigEndian(frames[(position + 16)..], epochs[i].Revision);
                position += 24;
            }
            Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(frames[..length], hash);
            return Convert.ToHexString(hash);
        }
        finally
        {
            // The frames hold key prefixes and tag addresses; the next renter must not see them
            if (rented != null) System.Buffers.ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    internal object? ReadLocal(string key)
    {
        if (_local == null) return null;
        return _local.TryGet(key, _local.Now, out var node) ? node.BoxedValue : null;
    }

    // Reuse immutable identity/configuration metadata when replacing the same local representation.
    // A diagnostic map lookup neither serves a response nor records another frequency/user request.
    internal HttpStorageInfo? FindLocalInfo(string key)
        => _local != null && _local.Map.TryGetValue(key, out var node) ? node.Policy as HttpStorageInfo : null;

    // Fixed hashed client text, prepared once per adapter view. Slot identity retains the full exact suffix.
    internal sealed class UrlSlotPrefix(string text)
    {
        private readonly uint _slotHash = ContinueSlotHash(2166136261u, text);
        internal int Slot(ReadOnlySpan<char> target, out bool canonical)
        {
            canonical = target.StartsWith(text, StringComparison.Ordinal);
            return canonical ? (int)(ContinueSlotHash(_slotHash, target[text.Length..]) & (GenerationSlots - 1)) : HttpResponseStore.Slot(target);
        }
    }

    internal bool StoreLocal(string key, object payload, HttpStorageInfo info)
    {
        if (_local == null || info.Cost <= 0 || info.Cost > MaximumBytes || info.Deadline <= _time.GetUtcNow()) return false;
        var now = _local.Now;
        var deadline = Deadline(now, info.Deadline - _time.GetUtcNow());
        _local.Add(new ValueNode<object>(key, payload)
        {
            Weight = info.Cost,
            Tags = IndexTags(info, local: true),
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
        { Tags = IndexTags(info, local: false), LogicalExpiresAt = deadline, PhysicalExpiresAt = deadline, AbsoluteExpiresAt = deadline });
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
        lock (_timerGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return InvalidateUrlsCore(targets, caller);
        }
    }

    private ValueTask InvalidateUrlsCore(IEnumerable<string> targets, CancellationToken caller)
    {
        var distinct = targets.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var target in distinct)
        {
            _local?.RemoveByTag(LocalSlotTag(Slot(target)));
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

    private async Task<bool> AdvanceAsync(int slot, PendingEpoch pending, bool shutdown = false)
    {
        lock (_timerGate)
        {
            if (_disposed != 0 && !shutdown) return false;
            _advancesInFlight++;
        }
        try
        {
            bool confirmed;
            try { confirmed = await _distributed!.AdvanceHttpSlotAsync(slot, shutdown).ConfigureAwait(false); }
            catch { Queue(slot, pending); throw; }
            if (confirmed)
            {
                _pending.TryRemove(new KeyValuePair<int, PendingEpoch>(slot, pending));
                _telemetry.Invalidations("key", "distributed", 1);
            }
            else Queue(slot, pending);
            return confirmed;
        }
        finally
        {
            lock (_timerGate)
                if (--_advancesInFlight == 0 && _disposed != 0) _advancesDrained.TrySetResult();
        }
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

    internal async Task FlushRecoveryAsync(bool shutdown = false)
    {
        if (shutdown) await _recoveryGate.WaitAsync().ConfigureAwait(false);
        else
        {
            lock (_timerGate)
                if (_disposed != 0 || !_recoveryGate.Wait(0)) return;
        }
        try
        {
            foreach (var pair in _pending.ToArray())
            {
                if (!pair.Value.Queued) continue;
                try { _ = await AdvanceAsync(pair.Key, pair.Value, shutdown).ConfigureAwait(false); }
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
            {
                if (!node.IsPhysicallyExpired(_local.Now) && node.Policy is HttpStorageInfo { Selector: false } info)
                {
                    if (info.Id.Length == 0)
                    {
                        var identified = info with { Id = Identity(node.Key) };
                        // Memoize only this immutable snapshot; a concurrent writer keeps its own metadata.
                        _ = Interlocked.CompareExchange(ref node.Policy, identified, info);
                        info = identified;
                    }
                    result.Add(info);
                }
            }
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

    internal static string Identity(string representationKey)
        => string.Create(69, representationKey, static (text, key) =>
        {
            Span<byte> digest = stackalloc byte[32];
            HashIdentity(key, digest);
            "http:".AsSpan().CopyTo(text);
            const string hexadecimal = "0123456789ABCDEF";
            for (var i = 0; i < digest.Length; i++)
            {
                text[5 + 2 * i] = hexadecimal[digest[i] >> 4];
                text[6 + 2 * i] = hexadecimal[digest[i] & 15];
            }
        });

    private static void HashIdentity(string key, Span<byte> digest)
    {
        if (BitConverter.IsLittleEndian)
        {
            _ = SHA256.HashData(MemoryMarshal.AsBytes(key.AsSpan()), digest);
            return;
        }

        HashIdentityUtf16Streaming(key, digest);
    }

    // The frozen identity is UTF-16LE even on a different native byte order. Stream fixed-size chunks.
    internal static void HashIdentityUtf16Streaming(string key, Span<byte> digest)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> chunk = stackalloc byte[512];
        for (var offset = 0; offset < key.Length;)
        {
            var count = Math.Min(chunk.Length / 2, key.Length - offset);
            for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt16LittleEndian(chunk.Slice(i * 2), key[offset + i]);
            hash.AppendData(chunk[..(count * 2)]);
            offset += count;
        }
        _ = hash.GetHashAndReset(digest);
    }
    private static string UrlTag(string target) => "\0http-url:" + target;
    private static string ApplicationTag(string tag) => "\0http-tag:" + Convert.ToBase64String(LosslessUtf16.GetBytes(tag));
    private string[] IndexTags(HttpStorageInfo info, bool local)
    {
        // Adapter-owned tag snapshots are immutable. Weak keys avoid retaining retired client/options views.
        var application = _applicationIndexes.GetValue(info.Tags, static tags =>
        {
            var encoded = new string[tags.Length];
            for (var i = 0; i < tags.Length; i++) encoded[i] = ApplicationTag(tags[i]);
            return encoded;
        });
        var indexed = new string[application.Length + 1];
        application.CopyTo(indexed, 0);
        indexed[^1] = local ? LocalSlotTag(Slot(info.UrlTarget)) : UrlTag(info.UrlTarget);
        return indexed;
    }

    private string LocalSlotTag(int slot)
    {
        var tags = _localSlotTags!;
        var existing = Volatile.Read(ref tags[slot]);
        if (existing != null) return existing;
        var created = "\0http-slot:" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Interlocked.CompareExchange(ref tags[slot], created, null) ?? created;
    }
    internal static int Slot(string target) => Slot(target.AsSpan());
    internal static int Slot(ReadOnlySpan<char> target) => (int)(ContinueSlotHash(2166136261u, target) & (GenerationSlots - 1));
    private static uint ContinueSlotHash(uint hash, ReadOnlySpan<char> target)
    {
        foreach (var character in target) hash = unchecked((hash ^ character) * 16777619u);
        return hash;
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
        lock (_timerGate)
        {
            if (_disposed != 0) return;
            Volatile.Write(ref _disposed, 1);
            _retry?.Dispose();
            if (_advancesInFlight == 0) _advancesDrained.TrySetResult();
            _shutdown = ShutdownAsync();
            _ = _shutdown.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    private async Task ShutdownAsync()
    {
        await _advancesDrained.Task.ConfigureAwait(false);
        try
        {
            if (Distributed)
            {
                await FlushRecoveryAsync(shutdown: true).ConfigureAwait(false);
                if (!_pending.IsEmpty) _distributed!.ReportLostHttpRecoveries(_pending.Count);
                _pending.Clear();
            }
        }
        finally
        {
            _distributed?.ObserveHttpRecovery(null);
            _local?.Clear(); _observations.Clear();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task shutdown;
        lock (_timerGate) shutdown = _shutdown!;
        await shutdown.ConfigureAwait(false);
    }
}
