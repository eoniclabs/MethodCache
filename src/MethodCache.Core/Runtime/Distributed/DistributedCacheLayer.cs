using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using MethodCache.Core.Configuration;
using MethodCache.Core.Storage.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MethodCache.Core.Runtime.Distributed
{
    /// <summary>
    /// The distributed cache (L2) behind <see cref="Execution.InMemoryCacheManager"/>. Entries are stored in any
    /// <see cref="IDistributedCache"/> (Redis, SQL Server, ...) together with their tags and creation time.
    /// </summary>
    /// <remarks>
    /// <para>Tags are invalidated by timestamp, as in HybridCache and FusionCache: invalidating a tag stores the
    /// time it happened, and an entry read later counts as missing when one of its tags was invalidated after the
    /// entry was created. No reverse index from tags to keys is needed, so any distributed cache works. A removed
    /// key leaves a short-lived tombstone, so a load that started before the removal doesn't write its older value
    /// back afterwards.</para>
    /// <para>An optional <see cref="IBackplane"/> tells other nodes to drop their in-memory copies when an entry is
    /// written or removed, or a tag is invalidated.</para>
    /// <para>The distributed cache never fails or stalls a call. Each operation is bounded by
    /// <see cref="DistributedCacheOptions.OperationTimeout"/>; a failure opens a circuit breaker, and while it is
    /// open reads are misses and writes are skipped. Invalidations that could not be stored are queued and retried
    /// until the distributed cache accepts them; until then nothing is read from it, so an invalidated value
    /// can't come back.</para>
    /// </remarks>
    internal sealed class DistributedCacheLayer : IDisposable
    {
        /// <summary>Reserved tag that every entry implicitly carries; invalidating it clears the whole cache.</summary>
        internal const string ClearTag = "\u0000clear";

        /// <summary>How long a removed key blocks writes of values loaded before the removal.</summary>
        internal static readonly TimeSpan TombstoneLifetime = TimeSpan.FromMinutes(5);

        private const byte FormatVersion = 3;
        private const byte KindNull = 0;
        private const byte KindValue = 1;
        private const byte KindTombstone = 2;
        private const int HeaderSize = 1 + 1 + 8 + 8 + 4;
        private const int MaxTrackedTags = 10_000;

        private readonly IDistributedCache _cache;
        private readonly IBufferDistributedCache? _bufferCache;
        private readonly ISerializer _serializer;

        // Written into every entry, so values another serializer wrote read as misses instead of wrong values
        private readonly byte[] _contentType;
        private readonly IBackplane? _backplane;
        private readonly TimeProvider _time;
        private readonly ILogger _logger;
        private readonly string _entryPrefix;
        private readonly string _tagPrefix;
        private readonly long _tagCheckMilliseconds;
        private readonly long _clockSkewMilliseconds;
        private readonly TimeSpan _tagRetention;
        private readonly TimeSpan _timeout;
        private readonly TimeSpan _breakDuration;
        private readonly ConcurrentDictionary<string, TagStamp> _tagStamps = new(StringComparer.Ordinal);

        // Circuit breaker: Unix ms until which the cache is skipped (0 = closed); one caller probes after that
        private long _openUntil;
        private int _probing;

        // Invalidations the distributed cache didn't accept yet, retried by _retryTimer
        private readonly object _pendingLock = new();
        private readonly HashSet<string> _pendingKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _pendingTags = new(StringComparer.Ordinal);
        private volatile bool _hasPending;
        private ITimer? _retryTimer;
        private int _flushing;

        private Action<string>? _onRemoteKeyInvalidated;
        private Action<string>? _onRemoteTagInvalidated;
        private Action? _onRemoteCleared;
        private Action<Exception>? _onError;
        private int _subscribed;
        private int _disposed;

        public DistributedCacheLayer(
            IDistributedCache cache,
            ISerializer serializer,
            DistributedCacheOptions options,
            IBackplane? backplane = null,
            TimeProvider? timeProvider = null,
            ILogger? logger = null)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _bufferCache = cache as IBufferDistributedCache;
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _contentType = Encoding.UTF8.GetBytes(serializer.ContentType);
            if (_contentType.Length > byte.MaxValue)
            {
                throw new ArgumentException($"The serializer's content type must be at most {byte.MaxValue} bytes.", nameof(serializer));
            }
            ArgumentNullException.ThrowIfNull(options);

            _backplane = backplane;
            _time = timeProvider ?? TimeProvider.System;
            _logger = logger ?? NullLogger.Instance;
            _entryPrefix = options.KeyPrefix + "e:";
            _tagPrefix = options.KeyPrefix + "t:";
            _tagCheckMilliseconds = (long)options.TagCheckInterval.TotalMilliseconds;
            _clockSkewMilliseconds = (long)options.ClockSkewTolerance.TotalMilliseconds;
            _tagRetention = options.TagRetention;
            _timeout = options.OperationTimeout;
            _breakDuration = options.CircuitBreakerDuration;
        }

        /// <summary>Wall-clock time in Unix milliseconds, comparable across nodes.</summary>
        public long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();

        /// <summary>
        /// Starts listening to the backplane, if there is one. Messages this node sent are ignored.
        /// <paramref name="onError"/> hears about every distributed cache or backplane failure.
        /// </summary>
        public void Attach(Action<string> onKeyInvalidated, Action<string> onTagInvalidated, Action onCleared, Action<Exception> onError)
        {
            _onRemoteKeyInvalidated = onKeyInvalidated;
            _onRemoteTagInvalidated = onTagInvalidated;
            _onRemoteCleared = onCleared;
            _onError = onError;

            if (_backplane != null && Interlocked.Exchange(ref _subscribed, 1) == 0)
            {
                _ = SubscribeAsync(_backplane);
            }
        }

        /// <summary>
        /// Reads an entry. Entries that expired, or whose tags were invalidated after they were created, count as
        /// missing, and so does everything while the distributed cache is unavailable.
        /// </summary>
        public async ValueTask<DistributedEntry<T>> TryGetAsync<T>(string key)
        {
            if (_hasPending || !TryEnter())
            {
                return default;
            }

            var read = await TryRunAsync(ReadAsync, key, "read").ConfigureAwait(false);
            if (!read.Succeeded || read.Value is not { } data ||
                !TryReadHeader(new ReadOnlySequence<byte>(data), _contentType, out var header, out var payload) ||
                header.Kind == KindTombstone)
            {
                return default;
            }

            var now = Now;
            if (header.ExpiresAt <= now)
            {
                return default;
            }

            var invalidated = await TryRunAsync(static (layer, header, ct) => layer.IsInvalidatedAsync(header.CreatedAt, header.Tags, ct), header, "read tags").ConfigureAwait(false);
            if (!invalidated.Succeeded || invalidated.Value)
            {
                return default;
            }

            T? value = default;
            if (header.Kind == KindValue)
            {
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

            return new DistributedEntry<T>(value, header.Tags, TimeSpan.FromMilliseconds(header.ExpiresAt - now));
        }

        /// <summary>
        /// Writes an entry created at <paramref name="createdAt"/> (when its value was read from the source), so tag
        /// invalidations that happened while the value was loading make it stale, and a key removed since then is
        /// not written back. <paramref name="invalidated"/> is checked again after the write. Never throws.
        /// </summary>
        public async Task WriteAsync<T>(string key, T value, string[]? tags, long createdAt, TimeSpan lifetime, Func<bool>? invalidated = null)
        {
            if (lifetime > _tagRetention)
            {
                lifetime = _tagRetention;
            }

            var lifetimeMilliseconds = (long)lifetime.TotalMilliseconds;
            if (lifetimeMilliseconds <= 0 || _hasPending || !TryEnter())
            {
                return;
            }

            // A key removed after this value was read must stay removed
            var current = await TryRunAsync(ReadAsync, key, "read").ConfigureAwait(false);
            if (!current.Succeeded)
            {
                return;
            }

            if (current.Value is { } existing &&
                TryReadHeader(new ReadOnlySequence<byte>(existing), _contentType, out var header, out _) &&
                header.Kind == KindTombstone && header.CreatedAt >= createdAt - _clockSkewMilliseconds)
            {
                return;
            }

            var buffer = new ArrayBufferWriter<byte>(256);
            var kind = value is null ? KindNull : KindValue;
            WriteHeader(buffer, _contentType, kind, createdAt, Now + lifetimeMilliseconds, tags);
            if (value is not null)
            {
                try
                {
                    _serializer.Serialize(value, buffer);
                }
                catch (Exception ex)
                {
                    // Only this value can't be shared; the cache itself is fine, so the breaker stays closed
                    _logger.LogWarning(ex, "MethodCache could not serialize a value of type {Type} for the distributed cache; it is cached in memory only.", typeof(T));
                    _onError?.Invoke(ex);
                    return;
                }
            }

            var entry = new Entry(key, buffer.WrittenMemory, TimeSpan.FromMilliseconds(lifetimeMilliseconds));
            if (!(await TryRunAsync(StoreAsync, entry, "write").ConfigureAwait(false)).Succeeded)
            {
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
        /// Removes keys everywhere, leaving tombstones so loads that started earlier don't write them back. Never
        /// throws: keys the distributed cache didn't accept are retried in the background.
        /// </summary>
        public Task RemoveAsync(IReadOnlyCollection<string> keys) => InvalidateAsync(keys, Array.Empty<string>());

        /// <summary>
        /// Records that the tags were invalidated now, so every entry created before carrying them counts as missing.
        /// Never throws: tags the distributed cache didn't accept are retried in the background.
        /// </summary>
        public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags) => InvalidateAsync(Array.Empty<string>(), tags);

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
                RememberStamp(tag, new TagStamp(now, now));
            }

            if (_hasPending || !TryEnter() || !await StoreInvalidationsAsync(keys, tags, now).ConfigureAwait(false))
            {
                Queue(keys, tags);
            }
        }

        /// <summary>Stores tombstones and tag stamps, then tells other nodes. False when the cache refused any.</summary>
        private async Task<bool> StoreInvalidationsAsync(IReadOnlyCollection<string> keys, IReadOnlyCollection<string> tags, long now)
        {
            var stamp = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(stamp, now);
            var tombstone = new ArrayBufferWriter<byte>(64);
            WriteHeader(tombstone, _contentType, KindTombstone, now, now + (long)TombstoneLifetime.TotalMilliseconds, tags: null);
            var data = tombstone.WrittenMemory;

            var results = await Task.WhenAll(
                tags.Select(tag => TryRunAsync(StoreStampAsync, (tag, stamp), "invalidate a tag"))
                    .Concat(keys.Select(key => TryRunAsync(StoreAsync, new Entry(key, data, TombstoneLifetime), "remove a key"))))
                .ConfigureAwait(false);
            if (results.Any(static result => !result.Succeeded))
            {
                return false;
            }

            foreach (var tag in tags)
            {
                await PublishAsync(static (backplane, tag, ct) => backplane.PublishTagInvalidationAsync(tag, ct), tag).ConfigureAwait(false);
            }

            foreach (var key in keys)
            {
                await PublishAsync(static (backplane, key, ct) => backplane.PublishInvalidationAsync(key, ct), key).ConfigureAwait(false);
            }

            return true;
        }

        /// <summary>
        /// Makes every entry written so far count as missing. The distributed cache cannot enumerate keys, so this
        /// invalidates a tag that every entry implicitly carries.
        /// </summary>
        public Task ClearAsync() => InvalidateTagsAsync(new[] { ClearTag });

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _retryTimer?.Dispose();
            }
        }

        // ---------- operations, each bounded by the timeout ----------

        private static async Task<byte[]?> ReadAsync(DistributedCacheLayer layer, string key, CancellationToken cancellationToken)
        {
            if (layer._bufferCache == null)
            {
                return await layer._cache.GetAsync(layer._entryPrefix + key, cancellationToken).ConfigureAwait(false);
            }

            var buffer = new ArrayBufferWriter<byte>();
            return await layer._bufferCache.TryGetAsync(layer._entryPrefix + key, buffer, cancellationToken).ConfigureAwait(false)
                ? buffer.WrittenSpan.ToArray()
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

        private static async Task<bool> StoreStampAsync(DistributedCacheLayer layer, (string Tag, byte[] Stamp) state, CancellationToken cancellationToken)
        {
            var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = layer._tagRetention };
            await layer._cache.SetAsync(layer._tagPrefix + state.Tag, state.Stamp, options, cancellationToken).ConfigureAwait(false);
            return true;
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
            var bytes = await _cache.GetAsync(_tagPrefix + tag, cancellationToken).ConfigureAwait(false);
            var invalidatedAt = bytes is { Length: >= sizeof(long) } ? BinaryPrimitives.ReadInt64LittleEndian(bytes) : long.MinValue;
            return RememberStamp(tag, new TagStamp(invalidatedAt, now));
        }

        /// <summary>Remembers a tag's stamp, never replacing a newer one (a read can finish after an invalidation).</summary>
        private long RememberStamp(string tag, TagStamp stamp)
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
                static (_, current, added) => new TagStamp(Math.Max(current.InvalidatedAt, added.InvalidatedAt), added.CheckedAt),
                stamp).InvalidatedAt;
        }

        // ---------- circuit breaker ----------

        /// <summary>
        /// Whether to use the distributed cache now: yes while the breaker is closed; once its open period is over,
        /// only for the one caller that probes whether the cache is back.
        /// </summary>
        private bool TryEnter()
        {
            var openUntil = Volatile.Read(ref _openUntil);
            if (openUntil == 0)
            {
                return true;
            }

            return Now >= openUntil && Interlocked.CompareExchange(ref _probing, 1, 0) == 0;
        }

        private async Task<(bool Succeeded, TResult Value)> TryRunAsync<TState, TResult>(
            Func<DistributedCacheLayer, TState, CancellationToken, Task<TResult>> operation,
            TState state,
            string description)
        {
            try
            {
                using var timeout = new CancellationTokenSource(_timeout, _time);
                // WaitAsync stops waiting even when the cache client ignores the token
                var result = await operation(this, state, timeout.Token).WaitAsync(_timeout, _time).ConfigureAwait(false);
                Recovered();
                return (true, result);
            }
            catch (Exception ex)
            {
                Trip(ex, description);
                return (false, default!);
            }
        }

        private void Trip(Exception exception, string operation)
        {
            var wasClosed = Interlocked.Exchange(ref _openUntil, Now + (long)_breakDuration.TotalMilliseconds) == 0;
            Volatile.Write(ref _probing, 0);
            if (wasClosed)
            {
                _logger.LogWarning(exception, "The distributed cache failed to {Operation}; MethodCache uses only its in-memory cache for the next {Duration}.", operation, _breakDuration);
            }

            _onError?.Invoke(exception);
        }

        private void Recovered()
        {
            if (Volatile.Read(ref _openUntil) != 0 && Interlocked.Exchange(ref _openUntil, 0) != 0)
            {
                _logger.LogInformation("The distributed cache is available again.");
            }

            Volatile.Write(ref _probing, 0);
        }

        // ---------- invalidations the distributed cache didn't accept ----------

        private void Queue(IEnumerable<string> keys, IEnumerable<string> tags)
        {
            lock (_pendingLock)
            {
                _pendingKeys.UnionWith(keys);
                _pendingTags.UnionWith(tags);

                _hasPending = true;
                if (_retryTimer == null && Volatile.Read(ref _disposed) == 0)
                {
                    _logger.LogWarning("Invalidations could not reach the distributed cache; they are retried every {Interval}, and nothing is read from it until they succeed.", _breakDuration);
                    _retryTimer = _time.CreateTimer(static state => _ = ((DistributedCacheLayer)state!).FlushAsync(), this, _breakDuration, _breakDuration);
                }
            }
        }

        /// <summary>
        /// Retries queued invalidations. They stay queued, and the distributed cache unread, until all are stored.
        /// </summary>
        internal async Task FlushAsync()
        {
            if (Interlocked.Exchange(ref _flushing, 1) != 0)
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

                    keys = _pendingKeys.ToArray();
                    tags = _pendingTags.ToArray();
                }

                // Stamped now: entries written before this moment, while the cache was unreachable, count as stale
                if (!TryEnter() || !await StoreInvalidationsAsync(keys, tags, Now).ConfigureAwait(false))
                {
                    return;
                }

                lock (_pendingLock)
                {
                    _pendingKeys.ExceptWith(keys);
                    _pendingTags.ExceptWith(tags);
                    if (_pendingKeys.Count == 0 && _pendingTags.Count == 0)
                    {
                        _hasPending = false;
                        _retryTimer?.Dispose();
                        _retryTimer = null;
                        _logger.LogInformation("Stored {Count} queued invalidations in the distributed cache.", keys.Length + tags.Length);
                    }
                }
            }
            finally
            {
                Volatile.Write(ref _flushing, 0);
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

        private async Task SubscribeAsync(IBackplane backplane)
        {
            try
            {
                await backplane.SubscribeAsync(OnBackplaneMessageAsync).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
            }
        }

        private Task OnBackplaneMessageAsync(BackplaneMessage message)
        {
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

                    case BackplaneMessageType.TagInvalidation when message.Tag == ClearTag:
                    case BackplaneMessageType.ClearAll:
                        _tagStamps.Clear();
                        _onRemoteCleared?.Invoke();
                        break;

                    case BackplaneMessageType.TagInvalidation when message.Tag != null:
                        // Re-read the stamp the other node wrote rather than trusting this node's clock
                        _tagStamps.TryRemove(message.Tag, out _);
                        _onRemoteTagInvalidated?.Invoke(message.Tag);
                        break;
                }
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
            }

            return Task.CompletedTask;
        }

        // ---------- entry format ----------

        // Layout: version, kind (null value, value or tombstone), created (Unix ms), expires (Unix ms), tag count,
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
                    var length = Encoding.UTF8.GetByteCount(tag);
                    span = writer.GetSpan(sizeof(int) + length);
                    BinaryPrimitives.WriteInt32LittleEndian(span, length);
                    Encoding.UTF8.GetBytes(tag, span.Slice(sizeof(int)));
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
                !reader.TryRead(out var kind) || kind > KindTombstone ||
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
                    if (!reader.TryReadLittleEndian(out int length) || length < 0 || length > reader.Remaining)
                    {
                        return false;
                    }

                    tags[i] = Encoding.UTF8.GetString(reader.UnreadSequence.Slice(0, length));
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

        private readonly record struct TagStamp(long InvalidatedAt, long CheckedAt);

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
