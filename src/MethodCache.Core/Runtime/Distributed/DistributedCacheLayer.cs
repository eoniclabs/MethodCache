using MethodCache.Infrastructure;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using MethodCache.Extensibility;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MethodCache.Core.Runtime.Distributed
{
    /// <summary>
    /// The distributed cache (L2) behind <see cref="MethodCache.Extensibility.InMemoryCacheManager"/>. Entries are stored in an
    /// <see cref="IAtomicInvalidationStore"/> (Redis, SQL Server, ...) together with their tags and creation time.
    /// </summary>
    /// <remarks>
    /// <para>Tags are invalidated by monotonic timestamp: invalidating a tag stores the
    /// time it happened, and an entry read later counts as missing when one of its tags was invalidated after the
    /// entry was created. No reverse index from tags to keys is needed, with backend atomic writes that cannot move timestamps backwards. A removed
    /// key leaves a short-lived removal stamp, so a load that started before the removal doesn't leave its older
    /// value behind.</para>
    /// <para>An optional <see cref="IBackplane"/> tells other nodes to drop their in-memory copies when an entry is
    /// written or removed, or a tag is invalidated.</para>
    /// <para>The distributed cache never fails or stalls a call. Each operation is bounded by
    /// <see cref="DistributedCacheOptions.OperationTimeout"/>; a failure opens a circuit breaker, and while it is
    /// open reads are misses and writes are skipped. Invalidations that could not be stored are queued and retried
    /// until the distributed cache accepts them; until then nothing is read from it, so an invalidated value
    /// can't come back.</para>
    /// </remarks>
    internal sealed class DistributedCacheLayer : IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// Reserved tag that every entry implicitly carries; invalidating it clears the whole cache. Application tags
        /// are stored through <see cref="StoredTag"/>, so no tag text, however it was built, can name this one.
        /// </summary>
        internal const string ClearTag = "\u0000clear";

        /// <summary>How long a key's removal stamp keeps values loaded before the removal out of the cache.</summary>
        internal static readonly TimeSpan RemovalLifetime = TimeSpan.FromMinutes(5);

        private const byte FormatVersion = 4;
        private const byte KindNull = 0;
        private const byte KindValue = 1;
        private const int HeaderSize = 1 + 1 + 8 + 8 + 4;
        private const int MaxTrackedTags = 10_000;

        // Invalidations stored at once (each its own operation); a longer queue than MaxQueuedInvalidations becomes a clear
        private const int InvalidationBatchSize = 50;
        private const int MaxQueuedInvalidations = 10_000;

        // Flushes in a row where the cache stored some invalidations and refused others, before those become a clear
        private const int MaxRefusedFlushes = 3;

        private readonly IDistributedCache _cache;
        private readonly IAtomicInvalidationStore _stampStore;
        private readonly IBufferDistributedCache? _bufferCache;
        private readonly ISerializer _serializer;

        // Written into every entry, so values another serializer wrote read as misses instead of wrong values
        private readonly byte[] _contentType;
        private readonly IBackplane? _backplane;
        private readonly TimeProvider _time;
        private readonly ILogger _logger;
        private readonly string _entryPrefix;
        private readonly string _tagPrefix;
        private readonly string _removalPrefix;
        private readonly long _tagCheckMilliseconds;
        private readonly long _clockSkewMilliseconds;
        private readonly TimeSpan _tagRetention;
        private readonly TimeSpan _timeout;
        private readonly TimeSpan _breakDuration;
        private readonly ConcurrentDictionary<string, TagStamp> _tagStamps = new(StringComparer.Ordinal);

        // Method, declared and runtime types whose values didn't survive the serializer (always false); see RoundTrips
        private readonly ConcurrentDictionary<(string Method, Type Declared, Type Written), bool> _roundTrips = new();

        // Methods with a value shared through the distributed cache, so a later value that can't be shared must
        // remove it there
        private readonly ConcurrentDictionary<string, byte> _sharedMethods = new(StringComparer.Ordinal);

        // Tag invalidations and clears other nodes announced so far
        private long _remoteInvalidations;

        // Circuit breaker: Unix ms until which the cache is skipped (0 = closed); one caller probes after that.
        // _trips counts openings, so a probe can only close the opening it was let in for.
        private const long NotProbing = -1;
        private const string HealthCheckKey = "\u0000health";
        private readonly object _breakerLock = new();
        private long _openUntil;
        private int _probing;
        private long _trips;

        // Invalidations the distributed cache didn't accept yet, retried by _retryTimer
        private readonly object _pendingLock = new();
        private readonly HashSet<string> _pendingKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _pendingTags = new(StringComparer.Ordinal);
        private volatile bool _hasPending;
        private bool _flushedForShutdown;
        private ITimer? _retryTimer;

        // One flush at a time: the retry timer skips a flush while one runs, shutdown waits for it
        private readonly SemaphoreSlim _flushGate = new(1, 1);
        private int _refusedFlushes;

        private Action<string>? _onRemoteKeyInvalidated;
        private Action<string>? _onRemoteTagInvalidated;
        private Action? _onRemoteCleared;
        private Action<Exception>? _onError;
        private int _subscribed;
        private int _disposed;
        private readonly object _lifecycleLock = new();
        private Task _subscriptionTask = Task.CompletedTask;
        private Task? _shutdownTask;
        private volatile bool _subscriptionWaitTimedOut;

        // Keys whose write timed out, and when (in TimeProvider timestamps) to remove them; one removal per key
        private readonly ConcurrentDictionary<string, long> _lateRemovals = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _disposing = new();

        // Writes running now; shutdown waits for them before it flushes the queue
        private int _writesInFlight;
        private readonly TaskCompletionSource _writesDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DistributedCacheLayer(
            IDistributedCache cache,
            ISerializer serializer,
            DistributedCacheOptions options,
            IBackplane? backplane = null,
            TimeProvider? timeProvider = null,
            ILogger? logger = null)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _stampStore = cache as IAtomicInvalidationStore ?? throw new ArgumentException(
                DistributedCacheStoreValidation.NotAtomicMessage(cache.GetType()), nameof(cache));
            _bufferCache = cache as IBufferDistributedCache;
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _contentType = Encoding.UTF8.GetBytes(serializer.ContentType);
            if (_contentType.Length > byte.MaxValue)
            {
                throw new ArgumentException($"The serializer's content type must be at most {byte.MaxValue} bytes.", nameof(serializer));
            }
            ArgumentNullException.ThrowIfNull(options);
            if (!options.HasEffectiveTagRetention)
                throw new ArgumentException("TagRetention must exceed ClockSkewTolerance after truncation to whole milliseconds.", nameof(options));

            _backplane = backplane;
            _time = timeProvider ?? TimeProvider.System;
            _logger = logger ?? NullLogger.Instance;
            _entryPrefix = options.KeyPrefix + "e:";
            _tagPrefix = options.KeyPrefix + "t:";
            _removalPrefix = options.KeyPrefix + "r:";
            _tagCheckMilliseconds = (long)options.TagCheckInterval.TotalMilliseconds;
            _clockSkewMilliseconds = options.ClockSkewTolerance.Ticks / TimeSpan.TicksPerMillisecond;
            _tagRetention = options.TagRetention;
            _timeout = options.OperationTimeout;
            _breakDuration = options.CircuitBreakerDuration;
        }

        /// <summary>Wall-clock time in Unix milliseconds, comparable across nodes.</summary>
        public long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();

        /// <summary>False while the breaker is open or invalidations wait to be stored, so only memory is used.</summary>
        internal bool IsAvailable => Volatile.Read(ref _openUntil) == 0 && !_hasPending;

        /// <summary>
        /// Whether the distributed cache answers now. It is asked once, bounded by the operation timeout, so an outage
        /// shows before any cached call has run into it; a failure opens the breaker, and once the breaker's time is up
        /// this check is the probe that can close it again, so an idle app doesn't stay degraded.
        /// </summary>
        internal async Task<bool> CheckAvailableAsync(CancellationToken cancellationToken)
        {
            if (_hasPending || !TryEnter(out var probe))
            {
                return false;
            }

            // A cancelled check stops waiting; the probe itself still finishes within the timeout and frees the breaker
            var answered = await TryRunAsync(ReadRemovalAsync, HealthCheckKey, "answer a health check", probe)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            return answered.Succeeded;
        }

        /// <summary>
        /// Starts listening to the backplane, if there is one. Messages this node sent are ignored.
        /// <paramref name="onError"/> hears about every distributed cache or backplane failure.
        /// </summary>
        public void Attach(Action<string> onKeyInvalidated, Action<string> onTagInvalidated, Action onCleared, Action<Exception> onError)
        {
            lock (_lifecycleLock)
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                _onRemoteKeyInvalidated = onKeyInvalidated;
                _onRemoteTagInvalidated = onTagInvalidated;
                _onRemoteCleared = onCleared;
                _onError = onError;

                if (_backplane != null && Interlocked.Exchange(ref _subscribed, 1) == 0)
                {
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _subscriptionTask = completion.Task;
                    _ = SubscribeAsync(_backplane, completion);
                }
            }
        }

        /// <summary>
        /// Reads an entry. Entries that expired, or whose tags were invalidated after they were created, count as
        /// missing, and so does everything while the distributed cache is unavailable.
        /// </summary>
        public async ValueTask<DistributedEntry<T>> TryGetAsync<T>(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_hasPending || !TryEnter(out var probe))
            {
                return default;
            }

            var read = await TryRunAsync(ReadAsync, key, "read", probe, cancellationToken).ConfigureAwait(false);
            if (!read.Succeeded || read.Value is not { } data ||
                !TryReadHeader(new ReadOnlySequence<byte>(data), _contentType, out var header, out var payload))
            {
                return default;
            }

            var now = Now;
            if (header.ExpiresAt <= now)
            {
                return default;
            }

            var invalidated = await TryRunAsync(static (layer, header, ct) => layer.IsInvalidatedAsync(header.CreatedAt, header.Tags, ct), header, "read tags", probe, cancellationToken).ConfigureAwait(false);
            if (!invalidated.Succeeded || invalidated.Value)
            {
                return default;
            }

            T? value = default;
            if (header.Kind == KindValue)
            {
                // A value is never written without its bytes, so this entry was cut short; serializers may read
                // nothing as a default value, which would be served as a wrong one
                if (payload.IsEmpty)
                {
                    return default;
                }

                try
                {
                    value = _serializer.Deserialize<T>(payload);
                }
                catch (Exception ex)
                {
                    // A value this type can't read, e.g. after a type changed: a miss, and the load overwrites it
                    _onError?.Invoke(ex);
                    return default;
                }
            }

            // No write asks for more than the tag retention; a damaged expiry far in the future must not overflow
            var remaining = Math.Min(header.ExpiresAt - now, _tagRetention.Ticks / TimeSpan.TicksPerMillisecond);
            return new DistributedEntry<T>(value, Convert(header.Tags, ApplicationTag), TimeSpan.FromMilliseconds(remaining));
        }

        /// <summary>
        /// Writes an entry created at <paramref name="createdAt"/> (when its value was read from the source), so tag
        /// invalidations that happened while the value was loading make it stale. A loaded value whose key was removed
        /// since is not left behind; an explicit set (<paramref name="replaces"/>) replaces whatever is stored, a
        /// removal included; when it can't be stored, the key is removed everywhere instead (now, or once the cache
        /// answers), so no node keeps the value it replaced. <paramref name="invalidated"/> is checked again after the
        /// write. Never throws.
        /// </summary>
        public async Task WriteAsync<T>(string key, T value, string[]? tags, long createdAt, TimeSpan lifetime, Func<bool>? invalidated = null, bool replaces = false, string? methodName = null)
        {
            // Shutdown waits for writes in flight (each bounded by the timeout), so the removal a write that times
            // out or fails leaves behind is flushed with the rest of the queue
            Interlocked.Increment(ref _writesInFlight);
            try
            {
                // Counted before this check, and Dispose sets the flag before shutdown reads the count: so either
                // shutdown waits for this write, or the write sees it is shutting down and stores nothing
                if (Volatile.Read(ref _disposed) != 0)
                {
                    if (replaces)
                    {
                        // Removed with the queue if shutdown hasn't flushed it yet, else logged as lost
                        Queue(new[] { key }, Array.Empty<string>());
                    }

                    return;
                }

                await WriteCoreAsync(key, value, tags, createdAt, lifetime, invalidated, replaces, methodName).ConfigureAwait(false);
            }
            finally
            {
                if (Interlocked.Decrement(ref _writesInFlight) == 0 && Volatile.Read(ref _disposed) != 0)
                {
                    _writesDrained.TrySetResult();
                }
            }
        }

        private async Task WriteCoreAsync<T>(string key, T value, string[]? tags, long createdAt, TimeSpan lifetime, Func<bool>? invalidated, bool replaces, string? methodName)
        {
            // The metadata that can invalidate this value must outlive it, including time spent loading or
            // writing it. Anchor its logical expiry to the source read, not the later publication time.
            var now = Now;
            var expiresAt = Math.Min(now + (long)lifetime.TotalMilliseconds,
                createdAt + _tagRetention.Ticks / TimeSpan.TicksPerMillisecond - _clockSkewMilliseconds);
            var lifetimeMilliseconds = expiresAt - now;
            if (lifetimeMilliseconds <= 0)
            {
                if (replaces)
                {
                    await RemoveAsync(new[] { key }).ConfigureAwait(false);
                }
                return;
            }

            if (_hasPending)
            {
                // An explicit set replaces what other nodes hold, so when it can't be shared the old value is
                // removed everywhere once the cache answers, as after a write that failed
                if (replaces)
                {
                    Queue(new[] { key }, Array.Empty<string>());
                }

                return;
            }

            // A load that took longer than removal stamps live could miss a removal made while it ran, so its value
            // stays in this node's memory only
            if (!replaces && Now - createdAt >= (long)RemovalLifetime.TotalMilliseconds - _clockSkewMilliseconds)
            {
                return;
            }

            // A method whose values of this type can't be shared isn't serialized again
            if (value is not null && _roundTrips.ContainsKey(RoundTripKey(methodName, value)))
            {
                if (MayHoldAnEarlierValue(replaces, methodName))
                {
                    await RemoveAsync(new[] { key }).ConfigureAwait(false);
                }

                return;
            }

            var buffer = new ArrayBufferWriter<byte>(256);
            WriteHeader(buffer, _contentType, value is null ? KindNull : KindValue, createdAt, expiresAt, Convert(tags, StoredTag));
            var payloadStart = buffer.WrittenCount;
            if (value is not null)
            {
                try
                {
                    _serializer.Serialize(value, buffer);
                }
                catch (Exception ex)
                {
                    // Only these values can't be shared; the cache itself is fine. Said once, like a value that
                    // doesn't read back, rather than on every write.
                    if (_roundTrips.TryAdd(RoundTripKey(methodName, value), false))
                    {
                        _logger.LogWarning(ex, "MethodCache could not serialize a value of type {Type} from {Method} for the distributed cache; its values of this type are cached in memory only.", value.GetType(), methodName ?? "SetAsync");
                    }

                    _onError?.Invoke(ex);

                    // Other nodes must not keep the value this one replaced
                    if (MayHoldAnEarlierValue(replaces, methodName))
                    {
                        await RemoveAsync(new[] { key }).ConfigureAwait(false);
                    }

                    return;
                }

                // A value the serializer can't read back as it was would be a different value on every other node
                if (!RoundTrips(methodName, value, buffer.WrittenMemory.Slice(payloadStart)))
                {
                    // An earlier value of the method (an Animal before this Dog, say) may still be there for the key
                    if (MayHoldAnEarlierValue(replaces, methodName))
                    {
                        await RemoveAsync(new[] { key }).ConfigureAwait(false);
                    }

                    return;
                }
            }

            // A null is shared too, so a later value that can't be removes it
            _sharedMethods.TryAdd(methodName ?? string.Empty, 0);

            if (!TryEnter(out var probe))
            {
                if (replaces)
                {
                    Queue(new[] { key }, Array.Empty<string>());
                }

                return;
            }

            var entry = new Entry(key, buffer.WrittenMemory, TimeSpan.FromMilliseconds(lifetimeMilliseconds));
            var written = await TryRunAsync(StoreAsync, entry, "write", probe, tripOnTimeout: false).ConfigureAwait(false);
            if (written.TimedOut)
            {
                // Says more about this value (a large one, say) than about the cache, which other operations may
                // reach fine: the breaker stays closed and reads go on. The write may still land, after a removal it
                // never saw, so the key is removed everywhere once that write has had time to finish.
                _logger.LogWarning("MethodCache could not write a value of type {Type} to the distributed cache within {Timeout}; it is removed from there again in {Delay}.", typeof(T), _timeout, _breakDuration);
                ScheduleLateRemoval(key);
                return;
            }

            if (!written.Succeeded)
            {
                // A failed write may still land later, after a removal it never saw, so the key is removed again
                // once the cache answers
                Queue(new[] { key }, Array.Empty<string>());
                return;
            }

            // A removal stores its stamp before it removes the entry, so checking the stamp after this write sees
            // every removal that could have come before the write, without a lock: removed after it, the entry is
            // gone anyway. A loaded value yields to removals made since it was read, allowing for clock skew; an
            // explicit set only to removals made since it started. A removal stamped in the same millisecond can't
            // be ordered, so it wins.
            var removedAt = await TryRunAsync(ReadRemovalAsync, key, "read a removal", probe).ConfigureAwait(false);
            var removedSince = removedAt.Succeeded &&
                removedAt.Value >= (replaces ? createdAt : createdAt - _clockSkewMilliseconds);
            if (!removedAt.Succeeded || removedSince)
            {
                // Removed since, or unknown: then the value comes out once the cache answers again
                if (!removedAt.Succeeded || !(await TryRunAsync(DeleteAsync, key, "remove a key", probe).ConfigureAwait(false)).Succeeded)
                {
                    Queue(new[] { key }, Array.Empty<string>());
                }

                return;
            }

            // Other nodes may hold an older value in memory
            await PublishAsync(static (backplane, key, ct) => backplane.PublishInvalidationAsync(key, ct), key).ConfigureAwait(false);

            if (invalidated?.Invoke() == true)
            {
                await RemoveAsync(new[] { key }).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Whether this value from <paramref name="methodName"/> comes back from the serializer as it was written: it
        /// is read back and written again, both writes must match (unless the serializer never writes the same bytes
        /// twice), and so must the value's type. Every value is
        /// checked, since one with default data (an <c>Id</c> of 0 behind a private setter) can pass where the next
        /// one loses data; once one fails, every value of that method and runtime type stays in memory only. A collection declared
        /// as an interface or abstract type may come back as another implementation of it (an array as a
        /// <c>List&lt;T&gt;</c>, say), since only its elements are written. A value that doesn't come back (private
        /// setters, a derived type returned as its base type, <c>object</c>) is kept in memory only, with one warning
        /// per method and runtime type.
        /// </summary>
        private bool RoundTrips<T>(string? methodName, T value, ReadOnlyMemory<byte> payload)
        {
            var written = value!.GetType();
            var method = RoundTripKey(methodName, value);
            if (_roundTrips.ContainsKey(method))
            {
                return false;
            }

            bool roundTrips;
            try
            {
                var read = _serializer.Deserialize<T>(new ReadOnlySequence<byte>(payload));
                var again = new ArrayBufferWriter<byte>(Math.Max(1, payload.Length));
                if (read is not null)
                {
                    _serializer.Serialize(read, again);
                }

                roundTrips = read is not null && SameType(typeof(T), written, read.GetType()) &&
                    (again.WrittenSpan.SequenceEqual(payload.Span) || !Deterministic(value, payload));
            }
            catch (Exception)
            {
                roundTrips = false;
            }

            if (roundTrips)
            {
                return true;
            }

            if (_roundTrips.TryAdd(method, false))
            {
                _logger.LogWarning(
                    "MethodCache keeps the results of {Method} of type {Type} (declared as {Declared}) in memory only: {Serializer} doesn't read them back unchanged, for example because of private setters, a derived type returned as its base type, or object. To share them between instances, register a serializer that does: a JsonCacheSerializer with your own JsonSerializerOptions or JsonSerializerContext (for example [JsonInclude] on the setter, [JsonDerivedType] on the base type), or your own ISerializer.",
                    methodName ?? "SetAsync", written, typeof(T), _serializer.GetType().Name);
            }

            return false;
        }

        /// <summary>
        /// Whether writing <paramref name="value"/> again gives <paramref name="payload"/>. A serializer that doesn't
        /// (encryption with a fresh nonce, say) can't be judged by its bytes, so only the type is checked for it.
        /// </summary>
        private bool Deterministic<T>(T value, ReadOnlyMemory<byte> payload)
        {
            var again = new ArrayBufferWriter<byte>(Math.Max(1, payload.Length));
            _serializer.Serialize(value, again);
            return again.WrittenSpan.SequenceEqual(payload.Span);
        }

        /// <summary>
        /// Whether the distributed cache may hold an earlier value for a key whose new value can't be shared: one
        /// this set replaces, or one the method shared before (a null, or an <c>Animal</c> before this <c>Dog</c>).
        /// </summary>
        private bool MayHoldAnEarlierValue(bool replaces, string? methodName)
            => replaces || _sharedMethods.ContainsKey(methodName ?? string.Empty);

        private static (string Method, Type Declared, Type Written) RoundTripKey<T>(string? methodName, T value)
            => (methodName ?? string.Empty, typeof(T), value!.GetType());

        /// <summary>
        /// Whether a value read back as <paramref name="read"/> is the value written as <paramref name="written"/>: the
        /// same type, or, for a collection declared as an interface or abstract type, any implementation of it, since
        /// only its elements are written and identical bytes mean identical elements.
        /// </summary>
        private static bool SameType(Type declared, Type written, Type read)
            => read == written ||
               ((declared.IsInterface || declared.IsAbstract) && declared != typeof(object) &&
                typeof(System.Collections.IEnumerable).IsAssignableFrom(declared) && declared.IsAssignableFrom(read));

        /// <summary>
        /// Removes a key whose write timed out once that write had time to land. Another timed-out write of the key
        /// pushes the removal back instead of adding one, so a slow cache doesn't pile up a delay per write.
        /// </summary>
        private void ScheduleLateRemoval(string key)
        {
            var due = _time.GetTimestamp() + (long)(_breakDuration.TotalSeconds * _time.TimestampFrequency);
            while (true)
            {
                if (_lateRemovals.TryAdd(key, due))
                {
                    _ = RemoveAfterLateWriteAsync(key);
                    return;
                }

                if (_lateRemovals.TryGetValue(key, out var scheduled) && _lateRemovals.TryUpdate(key, Math.Max(due, scheduled), scheduled))
                {
                    return;
                }
            }
        }

        private async Task RemoveAfterLateWriteAsync(string key)
        {
            try
            {
                while (_lateRemovals.TryGetValue(key, out var due))
                {
                    var wait = due - _time.GetTimestamp();
                    if (wait > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds((double)wait / _time.TimestampFrequency), _time, _disposing.Token).ConfigureAwait(false);
                    }
                    else if (_lateRemovals.TryRemove(new KeyValuePair<string, long>(key, due)))
                    {
                        await RemoveAsync(new[] { key }).ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Disposed: shutdown removes the key along with the other queued invalidations (Dispose moved the
                // keys scheduled before it; this is one scheduled after)
                lock (_pendingLock)
                {
                    if (_lateRemovals.TryRemove(key, out _))
                    {
                        AddPending(new[] { key }, Array.Empty<string>());
                        _hasPending = true;
                    }
                }
            }
        }

        /// <summary>
        /// Removes keys everywhere, leaving removal stamps so loads that started earlier don't leave their values. Never
        /// throws: keys the distributed cache didn't accept are retried in the background.
        /// </summary>
        public Task RemoveAsync(IReadOnlyCollection<string> keys) => InvalidateAsync(keys, Array.Empty<string>());

        /// <summary>
        /// Records that the tags were invalidated now, so every entry created before carrying them counts as missing.
        /// Never throws: tags the distributed cache didn't accept are retried in the background.
        /// </summary>
        public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags) => InvalidateAsync(Array.Empty<string>(), tags.Select(StoredTag).ToArray());

        private async Task InvalidateAsync(IReadOnlyCollection<string> keys, IReadOnlyCollection<string> tags)
        {
            if (keys.Count == 0 && tags.Count == 0)
            {
                return;
            }

            // Stamps are remembered at once, so this node's reads respect the invalidation whatever the cache says
            var now = Now;
            foreach (var tag in tags)
            {
                _ = RememberStamp(tag, new TagStamp(now, now));
            }

            if (_hasPending || !TryEnter(out var probe))
            {
                Queue(keys, tags);
                return;
            }

            var refused = await StoreInvalidationsAsync(keys, tags, now, probe, force: false).ConfigureAwait(false);
            if (refused.Count > 0)
            {
                Queue(refused.Keys, refused.Tags);
            }
        }

        /// <summary>
        /// Stores removal and tag stamps, then removes the entries and tells other nodes. Works in batches, so a
        /// large backlog never sends the cache more at once than it can do within the timeout; stops at the first
        /// batch the cache refused part of, and when the breaker opened meanwhile. <paramref name="force"/> ignores
        /// the breaker and starts no batch after one operation timeout (at shutdown). Returns what wasn't stored.
        /// </summary>
        private async Task<Refused> StoreInvalidationsAsync(IReadOnlyCollection<string> keys, IReadOnlyCollection<string> tags, long now, long probe, bool force)
        {
            var deadline = force ? Now + (long)_timeout.TotalMilliseconds : long.MaxValue;
            var items = tags.Select(static tag => (IsTag: true, Value: tag)).Concat(keys.Select(static key => (IsTag: false, Value: key))).ToList();
            var refused = new Refused();
            for (var start = 0; start < items.Count; start += InvalidationBatchSize)
            {
                var batch = items.GetRange(start, Math.Min(InvalidationBatchSize, items.Count - start));
                if (refused.Count > 0 || (start > 0 && (force ? Now >= deadline : !TryEnter(out probe))))
                {
                    refused.AddRange(batch);
                    continue;
                }

                // Stamps first: a load that writes its value after this checks the removal stamp and takes it out again
                var stamped = await Task.WhenAll(batch.Select(item => item.IsTag
                        ? TryRunAsync(StoreStampAsync, (_tagPrefix + item.Value, now, _tagRetention), "invalidate a tag", probe)
                        : TryRunAsync(StoreStampAsync, (_removalPrefix + item.Value, now, RemovalLifetime), "remove a key", probe)))
                    .ConfigureAwait(false);
                var removed = await Task.WhenAll(batch.Select((item, i) => item.IsTag || !stamped[i].Succeeded
                        ? Task.FromResult(stamped[i])
                        : TryRunAsync(DeleteAsync, item.Value, "remove a key", probe)))
                    .ConfigureAwait(false);

                var stored = new List<(bool IsTag, string Value)>(batch.Count);
                for (var i = 0; i < batch.Count; i++)
                {
                    if (removed[i].Succeeded)
                    {
                        stored.Add(batch[i]);
                    }
                    else
                    {
                        refused.Add(batch[i]);
                    }
                }

                // The cache answered part of this batch, so it refuses particular invalidations, not all of them
                refused.Answered = refused.Count > 0 && stored.Count > 0;

                // Published together, so a hung backplane holds the caller for one timeout, not one per key or tag
                await Task.WhenAll(stored.Select(item => item.IsTag
                        ? PublishAsync(static (backplane, tag, ct) => backplane.PublishTagInvalidationAsync(tag, ct), item.Value)
                        : PublishAsync(static (backplane, key, ct) => backplane.PublishInvalidationAsync(key, ct), item.Value)))
                    .ConfigureAwait(false);
            }

            return refused;
        }

        /// <summary>
        /// Makes every entry written so far count as missing. The distributed cache cannot enumerate keys, so this
        /// invalidates a tag that every entry implicitly carries.
        /// </summary>
        public Task ClearAsync() => InvalidateAsync(Array.Empty<string>(), new[] { ClearTag });

        /// <summary>
        /// The form an application tag takes in entries, stamps and backplane messages: a leading NUL is doubled, so
        /// a tag such as <c>"\0clear"</c> stays apart from <see cref="ClearTag"/>.
        /// </summary>
        internal static string StoredTag(string tag) => tag.StartsWith('\0') ? "\0" + tag : tag;

        /// <summary>The application tag for a <see cref="StoredTag"/>.</summary>
        internal static string ApplicationTag(string storedTag) => storedTag.StartsWith('\0') ? storedTag.Substring(1) : storedTag;

        /// <summary>Converts each tag; the array itself when no tag starts with NUL, as almost none do.</summary>
        private static string[]? Convert(string[]? tags, Func<string, string> convert)
            => tags == null || !Array.Exists(tags, static tag => tag.StartsWith('\0')) ? tags : Array.ConvertAll(tags, tag => convert(tag));

        public void Dispose()
        {
            lock (_lifecycleLock)
            {
                if (_disposed != 0) return;
                Volatile.Write(ref _disposed, 1);
                _onRemoteKeyInvalidated = null;
                _onRemoteTagInvalidated = null;
                _onRemoteCleared = null;
                _onError = null;
                lock (_pendingLock)
                {
                    _retryTimer?.Dispose();
                    _retryTimer = null;

                    // Keys whose write timed out are removed with the queue at shutdown rather than after a delay
                    // nobody waits for
                    if (!_lateRemovals.IsEmpty)
                    {
                        AddPending(_lateRemovals.Keys, Array.Empty<string>());
                        _lateRemovals.Clear();
                        _hasPending = true;
                    }
                }
                _disposing.Cancel();
                _shutdownTask = ShutdownAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            Dispose();
            Task shutdown;
            lock (_lifecycleLock) shutdown = _shutdownTask!;
            await shutdown.ConfigureAwait(false);
        }

        private async Task ShutdownAsync()
        {
            // Disposed is set, so the last write to finish completes this
            if (Volatile.Read(ref _writesInFlight) != 0)
            {
                await _writesDrained.Task.ConfigureAwait(false);
            }

            await FlushBeforeShutdownAsync().ConfigureAwait(false);
            if (_backplane == null || _subscribed == 0) return;
            try
            {
                await _subscriptionTask.WaitAsync(_timeout, _time).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _subscriptionWaitTimedOut = true;
            }
            await UnsubscribeAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Tries once, for about one operation timeout, to store invalidations still queued, whatever the breaker
        /// says: a queue doesn't outlive the process, and every instance would read the invalidated values again.
        /// </summary>
        private async Task FlushBeforeShutdownAsync()
        {
            bool nothingQueued;
            lock (_pendingLock)
            {
                // Checked under the lock Queue holds, so whatever is queued after this counts as lost
                nothingQueued = !_hasPending;
                _flushedForShutdown = nothingQueued;
            }

            if (nothingQueued)
            {
                return;
            }

            // A retry the timer started is awaited first, so what it re-queues is retried here, or counted as lost
            await FlushAsync(force: true).ConfigureAwait(false);

            int lost;
            lock (_pendingLock)
            {
                lost = _pendingKeys.Count + _pendingTags.Count;
                _pendingKeys.Clear();
                _pendingTags.Clear();
                _flushedForShutdown = true;
            }

            if (lost > 0)
            {
                _logger.LogWarning("MethodCache is shutting down with {Count} invalidations the distributed cache didn't accept; they are lost, so other instances may read the invalidated values from it until they expire.", lost);
            }
        }

        private async Task UnsubscribeAsync()
        {
            try
            {
                using var timeout = new CancellationTokenSource(_timeout, _time);
                await _backplane!.UnsubscribeAsync(timeout.Token).WaitAsync(_timeout, _time).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MethodCache could not finish unsubscribing from its backplane.");
            }
        }

        // ---------- operations, each bounded by the timeout ----------

        private static async Task<ReadOnlyMemory<byte>?> ReadAsync(DistributedCacheLayer layer, string key, CancellationToken cancellationToken)
        {
            if (layer._bufferCache == null)
            {
                var bytes = await layer._cache.GetAsync(layer._entryPrefix + key, cancellationToken).ConfigureAwait(false);
                return bytes == null ? null : new ReadOnlyMemory<byte>(bytes);
            }

            var buffer = new ArrayBufferWriter<byte>();
            return await layer._bufferCache.TryGetAsync(layer._entryPrefix + key, buffer, cancellationToken).ConfigureAwait(false)
                ? buffer.WrittenMemory
                : null;
        }

        private static async Task<bool> StoreAsync(DistributedCacheLayer layer, Entry entry, CancellationToken cancellationToken)
        {
            var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = entry.Lifetime };
            if (layer._bufferCache != null)
            {
                await layer._bufferCache.SetAsync(layer._entryPrefix + entry.Key, new ReadOnlySequence<byte>(entry.Data), options, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await layer._cache.SetAsync(layer._entryPrefix + entry.Key, entry.Data.ToArray(), options, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }

        private static async Task<bool> StoreStampAsync(DistributedCacheLayer layer, (string Key, long Timestamp, TimeSpan Lifetime) state, CancellationToken cancellationToken)
        {
            await layer._stampStore.WriteInvalidationStampAsync(state.Key, state.Timestamp, state.Lifetime, cancellationToken).ConfigureAwait(false);
            return true;
        }

        private static async Task<bool> DeleteAsync(DistributedCacheLayer layer, string key, CancellationToken cancellationToken)
        {
            await layer._cache.RemoveAsync(layer._entryPrefix + key, cancellationToken).ConfigureAwait(false);
            return true;
        }

        /// <summary>When the key was last removed, in Unix milliseconds; <see cref="long.MinValue"/> if not lately.</summary>
        private static async Task<long> ReadRemovalAsync(DistributedCacheLayer layer, string key, CancellationToken cancellationToken)
        {
            var bytes = await layer._cache.GetAsync(layer._removalPrefix + key, cancellationToken).ConfigureAwait(false);
            return bytes is { Length: >= sizeof(long) } ? BinaryPrimitives.ReadInt64LittleEndian(bytes) : long.MinValue;
        }

        private async Task<bool> IsInvalidatedAsync(long createdAt, string[]? tags, CancellationToken cancellationToken)
        {
            // An entry written by a node whose clock runs ahead looks newer than it is; the tolerance covers that
            var threshold = createdAt - _clockSkewMilliseconds;
            var now = Now;

            List<string>? unknown = null;
            if (IsInvalidatedLocally(ClearTag, threshold, now, ref unknown))
            {
                return true;
            }

            if (tags != null)
            {
                foreach (var tag in tags)
                {
                    if (IsInvalidatedLocally(tag, threshold, now, ref unknown))
                    {
                        return true;
                    }
                }
            }

            if (unknown == null)
            {
                return false;
            }

            // Stamps not known locally are read together, in one round trip
            var stamps = await Task.WhenAll(unknown.Select(tag => ReadStampAsync(tag, now, cancellationToken))).ConfigureAwait(false);
            return stamps.Any(stamp => stamp >= threshold);
        }

        private bool IsInvalidatedLocally(string tag, long threshold, long now, ref List<string>? unknown)
        {
            if (_tagStamps.TryGetValue(tag, out var known) && now - known.CheckedAt < _tagCheckMilliseconds)
            {
                return known.InvalidatedAt >= threshold;
            }

            (unknown ??= new List<string>()).Add(tag);
            return false;
        }

        private async Task<long> ReadStampAsync(string tag, long now, CancellationToken cancellationToken)
        {
            // An invalidation another node announces while this read is on the wire may not be in its answer
            var generation = Volatile.Read(ref _remoteInvalidations);
            var bytes = await _cache.GetAsync(_tagPrefix + tag, cancellationToken).ConfigureAwait(false);
            var invalidatedAt = bytes is { Length: >= sizeof(long) } ? BinaryPrimitives.ReadInt64LittleEndian(bytes) : long.MinValue;
            var stamp = new TagStamp(invalidatedAt, now);
            if (invalidatedAt > now + _clockSkewMilliseconds)
            {
                // Written by a node whose clock runs ahead, so taken as it is it would make every entry written
                // until that clock's time look invalidated. It happened by now: it counts from when this node first
                // read it, until this clock passes it
                var since = _tagStamps.TryGetValue(tag, out var known) && known.Ahead == invalidatedAt ? known.InvalidatedAt : now;
                stamp = new TagStamp(since, now, Ahead: invalidatedAt);
            }

            var remembered = RememberStamp(tag, stamp);
            if (Volatile.Read(ref _remoteInvalidations) != generation)
            {
                // Possibly older than that invalidation: not trusted, so the next check reads the stamp again
                _tagStamps.TryRemove(new KeyValuePair<string, TagStamp>(tag, remembered));
            }

            return remembered.InvalidatedAt;
        }

        /// <summary>Remembers a tag's stamp, never replacing a newer one (a read can finish after an invalidation).</summary>
        private TagStamp RememberStamp(string tag, TagStamp stamp)
        {
            // Tags can be per-entity ("user:42"), so keep the local copy bounded; forgotten stamps are re-read
            if (_tagStamps.Count >= MaxTrackedTags)
            {
                var stale = Now - _tagCheckMilliseconds;
                foreach (var pair in _tagStamps)
                {
                    if (pair.Value.CheckedAt < stale)
                    {
                        _tagStamps.TryRemove(pair);
                    }
                }

                if (_tagStamps.Count >= MaxTrackedTags)
                {
                    _tagStamps.Clear();
                }
            }

            return _tagStamps.AddOrUpdate(
                tag,
                static (_, added) => added,
                static (_, current, added) => added with { InvalidatedAt = Math.Max(current.InvalidatedAt, added.InvalidatedAt) },
                stamp);
        }

        // ---------- circuit breaker ----------

        /// <summary>
        /// Whether to use the distributed cache now: yes while the breaker is closed; once its open period is over,
        /// only for the one caller that probes whether the cache is back. <paramref name="probe"/> identifies that
        /// caller's probe, or is <see cref="NotProbing"/>.
        /// </summary>
        private bool TryEnter(out long probe)
        {
            probe = NotProbing;
            if (Volatile.Read(ref _openUntil) == 0)
            {
                return true;
            }

            lock (_breakerLock)
            {
                if (_openUntil == 0)
                {
                    return true;
                }

                if (Now < _openUntil || _probing != 0)
                {
                    return false;
                }

                _probing = 1;
                probe = _trips;
                return true;
            }
        }

        /// <summary>
        /// Runs one operation bounded by the timeout. A failure trips the breaker, except a timeout when
        /// <paramref name="tripOnTimeout"/> is false, which is only reported as <c>TimedOut</c>.
        /// </summary>
        private async Task<(bool Succeeded, TResult Value, bool TimedOut)> TryRunAsync<TState, TResult>(
            Func<DistributedCacheLayer, TState, CancellationToken, Task<TResult>> operation,
            TState state,
            string description,
            long probe,
            CancellationToken cancellationToken = default,
            bool tripOnTimeout = true)
        {
            try
            {
                using var timeout = new CancellationTokenSource(_timeout, _time);
                using var linked = cancellationToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken)
                    : null;
                // Stop the provider as well as the wait; uncooperative clients are still bounded.
                var result = await operation(this, state, linked?.Token ?? timeout.Token)
                    .WaitAsync(_timeout, _time, linked?.Token ?? CancellationToken.None).ConfigureAwait(false);

                // Only the probe may close the breaker: an operation let in before a failure tripped it says nothing
                // about whether the cache is back
                if (probe != NotProbing)
                {
                    Recovered(probe);
                }

                return (true, result, false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A caller leaving says nothing about backend health. Free a half-open probe for the next reader.
                ReleaseProbe(probe);
                throw;
            }
            catch (Exception ex) when (!tripOnTimeout && ex is TimeoutException or OperationCanceledException)
            {
                ReleaseProbe(probe);
                _onError?.Invoke(ex);
                return (false, default!, true);
            }
            catch (Exception ex)
            {
                Trip(ex, description);
                return (false, default!, false);
            }
        }

        /// <summary>Lets the next caller probe the cache when <paramref name="probe"/> ended without an answer.</summary>
        private void ReleaseProbe(long probe)
        {
            lock (_breakerLock)
            {
                if (probe != NotProbing && _trips == probe)
                {
                    _probing = 0;
                }
            }
        }

        private void Trip(Exception exception, string operation)
        {
            bool wasClosed;
            lock (_breakerLock)
            {
                wasClosed = _openUntil == 0;
                _trips++;
                Volatile.Write(ref _openUntil, Now + (long)_breakDuration.TotalMilliseconds);
                _probing = 0;
            }

            if (wasClosed)
            {
                _logger.LogWarning(exception, "The distributed cache failed to {Operation}; MethodCache uses only its in-memory cache for the next {Duration}.", operation, _breakDuration);
            }

            _onError?.Invoke(exception);
        }

        private void Recovered(long probe)
        {
            lock (_breakerLock)
            {
                // Tripped again since this probe was let in: that opening has its own probe
                if (_trips != probe || _openUntil == 0)
                {
                    return;
                }

                Volatile.Write(ref _openUntil, 0);
                _probing = 0;
            }

            _logger.LogInformation("The distributed cache is available again.");
        }

        // ---------- invalidations the distributed cache didn't accept ----------

        private void Queue(IEnumerable<string> keys, IEnumerable<string> tags)
        {
            lock (_pendingLock)
            {
                // Also while shutting down: shutdown flushes the queue (AddPending counts what comes after as lost)
                AddPending(keys, tags);

                _hasPending = true;
                if (_retryTimer == null && Volatile.Read(ref _disposed) == 0)
                {
                    _logger.LogWarning("Invalidations could not reach the distributed cache; they are retried every {Interval}, and nothing is read from it until they succeed.", _breakDuration);
                    _retryTimer = _time.CreateTimer(static state => _ = ((DistributedCacheLayer)state!).FlushAsync(), this, _breakDuration, _breakDuration);
                }
            }
        }

        /// <summary>
        /// Adds to the queue (under <see cref="_pendingLock"/>). A queued clear stands for everything else, since it is
        /// stamped when it is stored: so a queue that grew too long becomes one clear.
        /// </summary>
        private void AddPending(IEnumerable<string> keys, IEnumerable<string> tags)
        {
            if (_flushedForShutdown)
            {
                // Nothing reads the queue any more
                var count = keys.Count() + tags.Count();
                if (count > 0)
                {
                    _logger.LogWarning("MethodCache is shutting down with {Count} invalidations the distributed cache didn't accept; they are lost, so other instances may read the invalidated values from it until they expire.", count);
                }

                return;
            }

            if (_pendingTags.Contains(ClearTag))
            {
                return;
            }

            _pendingKeys.UnionWith(keys);
            _pendingTags.UnionWith(tags);
            if (_pendingTags.Contains(ClearTag) || _pendingKeys.Count + _pendingTags.Count > MaxQueuedInvalidations)
            {
                if (!_pendingTags.Contains(ClearTag))
                {
                    _logger.LogWarning("More than {Count} invalidations wait for the distributed cache; they are replaced by clearing it once it answers.", MaxQueuedInvalidations);
                }

                ReplaceQueueWithClear();
            }
        }

        private void ReplaceQueueWithClear()
        {
            _pendingKeys.Clear();
            _pendingTags.Clear();
            _pendingTags.Add(ClearTag);
        }

        /// <summary>
        /// Retries queued invalidations, a batch at a time; each one leaves the queue once it is stored. The
        /// distributed cache stays unread until the queue is empty. <paramref name="force"/> tries even while the
        /// breaker is open (at shutdown).
        /// </summary>
        internal async Task FlushAsync(bool force = false)
        {
            if (force)
            {
                await _flushGate.WaitAsync().ConfigureAwait(false);
            }
            else if (!_flushGate.Wait(0))
            {
                return;
            }

            try
            {
                string[] keys;
                string[] tags;
                lock (_pendingLock)
                {
                    if (!_hasPending)
                    {
                        return;
                    }

                    // Taken out of the queue, so an invalidation queued meanwhile stays queued; nothing is read
                    // from the distributed cache until the queue is empty
                    keys = _pendingKeys.ToArray();
                    tags = _pendingTags.ToArray();
                    _pendingKeys.Clear();
                    _pendingTags.Clear();
                }

                // Stamped now: entries written before this moment, while the cache was unreachable, count as stale
                // Nothing to store would let a probe in that never reports back, so skip the cache then
                var probe = NotProbing;
                Refused refused;
                if (keys.Length == 0 && tags.Length == 0)
                {
                    refused = new Refused();
                }
                else if (!force && !TryEnter(out probe))
                {
                    refused = Refused.All(keys, tags);
                }
                else
                {
                    refused = await StoreInvalidationsAsync(keys, tags, Now, probe, force).ConfigureAwait(false);

                    // Refused while the cache still answers a read: it refuses these invalidations, not everything
                    if (refused.Count > 0 && !refused.Answered && !force)
                    {
                        refused.Answered = (await TryRunAsync(ReadRemovalAsync, HealthCheckKey, "answer a health check", NotProbing).ConfigureAwait(false)).Succeeded;
                    }
                }

                lock (_pendingLock)
                {
                    AddPending(refused.Keys, refused.Tags);
                    _refusedFlushes = refused.Answered ? _refusedFlushes + 1 : refused.Count == 0 ? 0 : _refusedFlushes;
                    if (_refusedFlushes >= MaxRefusedFlushes && !_pendingTags.Contains(ClearTag))
                    {
                        // The cache answers, but keeps refusing some invalidation (a key it rejects, say): a clear
                        // covers it, so the rest of the queue, and reading the cache, aren't held up by it forever
                        _logger.LogWarning("The distributed cache keeps refusing {Count} queued invalidations; they are replaced by clearing it.", refused.Count);
                        ReplaceQueueWithClear();
                    }

                    if (_pendingKeys.Count == 0 && _pendingTags.Count == 0)
                    {
                        _hasPending = false;
                        _refusedFlushes = 0;
                        _retryTimer?.Dispose();
                        _retryTimer = null;
                        _logger.LogInformation("Stored {Count} queued invalidations in the distributed cache.", keys.Length + tags.Length);
                    }
                }
            }
            finally
            {
                _flushGate.Release();
            }
        }

        /// <summary>Invalidations the distributed cache didn't store.</summary>
        private sealed class Refused
        {
            public List<string> Keys { get; } = new();

            public List<string> Tags { get; } = new();

            public int Count => Keys.Count + Tags.Count;

            /// <summary>Whether the cache answered other operations while it refused these.</summary>
            public bool Answered { get; set; }

            public void Add((bool IsTag, string Value) item) => (item.IsTag ? Tags : Keys).Add(item.Value);

            public void AddRange(IEnumerable<(bool IsTag, string Value)> items)
            {
                foreach (var item in items)
                {
                    Add(item);
                }
            }

            public static Refused All(IEnumerable<string> keys, IEnumerable<string> tags)
            {
                var refused = new Refused();
                refused.Keys.AddRange(keys);
                refused.Tags.AddRange(tags);
                return refused;
            }
        }

        // ---------- backplane ----------

        private async Task PublishAsync(Func<IBackplane, string, CancellationToken, Task> publish, string value)
        {
            if (_backplane == null)
            {
                return;
            }

            try
            {
                using var timeout = new CancellationTokenSource(_timeout, _time);
                await publish(_backplane, value, timeout.Token).WaitAsync(_timeout, _time).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Other nodes' memory catches up when their entries expire; the distributed cache is already right
                _onError?.Invoke(ex);
            }
        }

        private async Task SubscribeAsync(IBackplane backplane, TaskCompletionSource completion)
        {
            try
            {
                await backplane.SubscribeAsync(OnBackplaneMessageAsync).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
            }
            finally
            {
                // A client that ignores cancellation can register after bounded shutdown has returned.
                if (_subscriptionWaitTimedOut)
                    await UnsubscribeAsync().ConfigureAwait(false);
                completion.TrySetResult();
            }
        }

        private Task OnBackplaneMessageAsync(BackplaneMessage message)
        {
            lock (_lifecycleLock)
            {
                if (_disposed != 0) return Task.CompletedTask;
                if (message.InstanceId == _backplane!.InstanceId)
                {
                    return Task.CompletedTask;
                }

                try
                {
                    switch (message.Type)
                    {
                        case BackplaneMessageType.KeyInvalidation when message.Key != null:
                            _onRemoteKeyInvalidated?.Invoke(message.Key);
                            break;

                        // Stamp reads still on the wire may predate the message: counted first, so they aren't
                        // remembered (see ReadStampAsync)
                        case BackplaneMessageType.TagInvalidation when message.Tag == ClearTag:
                        case BackplaneMessageType.ClearAll:
                            Interlocked.Increment(ref _remoteInvalidations);
                            _tagStamps.Clear();
                            _onRemoteCleared?.Invoke();
                            break;

                        case BackplaneMessageType.TagInvalidation when message.Tag != null:
                            // Re-read the stamp the other node wrote rather than trusting this node's clock
                            Interlocked.Increment(ref _remoteInvalidations);
                            _tagStamps.TryRemove(message.Tag, out _);
                            _onRemoteTagInvalidated?.Invoke(ApplicationTag(message.Tag));
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _onError?.Invoke(ex);
                }

                return Task.CompletedTask;
            }
        }

        // ---------- entry format ----------

        // Layout: version, kind (null value or value), created (Unix ms), expires (Unix ms), tag count,
        // then each tag as a length-prefixed UTF-8 string, then the content type and the serialized value.
        private static void WriteHeader(IBufferWriter<byte> writer, byte[] contentType, byte kind, long createdAt, long expiresAt, string[]? tags)
        {
            var span = writer.GetSpan(HeaderSize);
            span[0] = FormatVersion;
            span[1] = kind;
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(2), createdAt);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(10), expiresAt);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(18), tags?.Length ?? 0);
            writer.Advance(HeaderSize);

            if (tags != null)
            {
                foreach (var tag in tags)
                {
                    var length = checked(tag.Length * sizeof(char));
                    span = writer.GetSpan(sizeof(int) + length);
                    BinaryPrimitives.WriteInt32LittleEndian(span, length);
                    LosslessUtf16.Write(tag, span.Slice(sizeof(int)));
                    writer.Advance(sizeof(int) + length);
                }
            }

            span = writer.GetSpan(1 + contentType.Length);
            span[0] = (byte)contentType.Length;
            contentType.CopyTo(span.Slice(1));
            writer.Advance(1 + contentType.Length);
        }

        private static bool TryReadHeader(ReadOnlySequence<byte> data, byte[] contentType, out Header header, out ReadOnlySequence<byte> payload)
        {
            header = default;
            payload = default;

            var reader = new SequenceReader<byte>(data);
            if (!reader.TryRead(out var version) || version != FormatVersion ||
                !reader.TryRead(out var kind) || kind > KindValue ||
                !reader.TryReadLittleEndian(out long createdAt) ||
                !reader.TryReadLittleEndian(out long expiresAt) ||
                !reader.TryReadLittleEndian(out int tagCount) ||
                tagCount < 0 || tagCount > reader.Remaining)
            {
                return false;
            }

            string[]? tags = null;
            if (tagCount > 0)
            {
                tags = new string[tagCount];
                for (var i = 0; i < tagCount; i++)
                {
                    if (!reader.TryReadLittleEndian(out int length) || length < 0 || (length & 1) != 0 || length > reader.Remaining)
                    {
                        return false;
                    }

                    tags[i] = LosslessUtf16.Read(reader.Sequence.Slice(reader.Position, length));
                    reader.Advance(length);
                }
            }

            if (!reader.TryRead(out var contentTypeLength) ||
                contentTypeLength != contentType.Length ||
                !reader.IsNext(contentType, advancePast: true))
            {
                return false;
            }

            header = new Header(kind, createdAt, expiresAt, tags);
            payload = reader.UnreadSequence;
            return true;
        }

        private readonly record struct Header(byte Kind, long CreatedAt, long ExpiresAt, string[]? Tags);

        /// <summary>
        /// A tag's stamp as this node uses it, read from the cache at <paramref name="CheckedAt"/>.
        /// <paramref name="Ahead"/> is the stored stamp when it was ahead of this node's clock (see ReadStampAsync).
        /// </summary>
        private readonly record struct TagStamp(long InvalidatedAt, long CheckedAt, long Ahead = long.MinValue);

        private readonly record struct Entry(string Key, ReadOnlyMemory<byte> Data, TimeSpan Lifetime);
    }

    /// <summary>An entry read from the distributed cache; <see cref="Found"/> is false when there was none.</summary>
    internal readonly struct DistributedEntry<T>
    {
        public DistributedEntry(T? value, string[]? tags, TimeSpan remainingLifetime)
        {
            Found = true;
            Value = value;
            Tags = tags;
            RemainingLifetime = remainingLifetime;
        }

        public bool Found { get; }

        public T? Value { get; }

        public string[]? Tags { get; }

        public TimeSpan RemainingLifetime { get; }
    }
}
