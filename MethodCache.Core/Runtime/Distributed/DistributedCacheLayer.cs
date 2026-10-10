using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using MethodCache.Core.Configuration;
using MethodCache.Core.Storage.Abstractions;
using Microsoft.Extensions.Caching.Distributed;

namespace MethodCache.Core.Runtime.Distributed
{
    /// <summary>
    /// The distributed cache (L2) behind <see cref="Execution.InMemoryCacheManager"/>. Entries are stored in any
    /// <see cref="IDistributedCache"/> (Redis, SQL Server, ...) together with their tags and creation time.
    /// </summary>
    /// <remarks>
    /// <para>Tags are invalidated by timestamp, as in HybridCache and FusionCache: invalidating a tag stores the
    /// time it happened, and an entry read later counts as missing when one of its tags was invalidated after the
    /// entry was created. No reverse index from tags to keys is needed, so any distributed cache works.</para>
    /// <para>An optional <see cref="IBackplane"/> tells other nodes to drop their in-memory copies when an entry is
    /// written or removed, or a tag is invalidated.</para>
    /// </remarks>
    internal sealed class DistributedCacheLayer
    {
        /// <summary>Reserved tag that every entry implicitly carries; invalidating it clears the whole cache.</summary>
        internal const string ClearTag = "\u0000clear";

        private const byte FormatVersion = 2;
        private const int HeaderSize = 1 + 1 + 8 + 8 + 4;
        private const int MaxTrackedTags = 10_000;

        private readonly IDistributedCache _cache;
        private readonly IBufferDistributedCache? _bufferCache;
        private readonly ISerializer _serializer;

        // Written into every entry, so values another serializer wrote read as misses instead of wrong values
        private readonly byte[] _contentType;
        private readonly IBackplane? _backplane;
        private readonly TimeProvider _time;
        private readonly string _entryPrefix;
        private readonly string _tagPrefix;
        private readonly long _tagCheckMilliseconds;
        private readonly TimeSpan _tagRetention;
        private readonly ConcurrentDictionary<string, TagStamp> _tagStamps = new(StringComparer.Ordinal);

        private Action<string>? _onRemoteKeyInvalidated;
        private Action<string>? _onRemoteTagInvalidated;
        private Action? _onRemoteCleared;
        private Action<Exception>? _onBackplaneError;
        private int _subscribed;

        public DistributedCacheLayer(
            IDistributedCache cache,
            ISerializer serializer,
            DistributedCacheOptions options,
            IBackplane? backplane = null,
            TimeProvider? timeProvider = null)
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
            _entryPrefix = options.KeyPrefix + "e:";
            _tagPrefix = options.KeyPrefix + "t:";
            _tagCheckMilliseconds = (long)options.TagCheckInterval.TotalMilliseconds;
            _tagRetention = options.TagRetention;
        }

        /// <summary>Wall-clock time in Unix milliseconds, comparable across nodes.</summary>
        public long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();

        /// <summary>
        /// Starts listening to the backplane, if there is one. Messages this node sent are ignored.
        /// </summary>
        public void Attach(Action<string> onKeyInvalidated, Action<string> onTagInvalidated, Action onCleared, Action<Exception> onError)
        {
            _onRemoteKeyInvalidated = onKeyInvalidated;
            _onRemoteTagInvalidated = onTagInvalidated;
            _onRemoteCleared = onCleared;
            _onBackplaneError = onError;

            if (_backplane != null && Interlocked.Exchange(ref _subscribed, 1) == 0)
            {
                _ = SubscribeAsync(_backplane);
            }
        }

        /// <summary>
        /// Reads an entry. Entries that expired, or whose tags were invalidated after they were created, count as missing.
        /// </summary>
        public async ValueTask<DistributedEntry<T>> TryGetAsync<T>(string key, CancellationToken cancellationToken = default)
        {
            ReadOnlySequence<byte> data;
            if (_bufferCache != null)
            {
                var buffer = new ArrayBufferWriter<byte>();
                if (!await _bufferCache.TryGetAsync(_entryPrefix + key, buffer, cancellationToken).ConfigureAwait(false))
                {
                    return default;
                }

                data = new ReadOnlySequence<byte>(buffer.WrittenMemory);
            }
            else
            {
                var bytes = await _cache.GetAsync(_entryPrefix + key, cancellationToken).ConfigureAwait(false);
                if (bytes == null)
                {
                    return default;
                }

                data = new ReadOnlySequence<byte>(bytes);
            }

            if (!TryReadHeader(data, _contentType, out var header, out var payload))
            {
                return default;
            }

            var now = Now;
            if (header.ExpiresAt <= now ||
                await IsInvalidatedAsync(header.CreatedAt, header.Tags, cancellationToken).ConfigureAwait(false))
            {
                return default;
            }

            var value = header.HasValue ? _serializer.Deserialize<T>(payload) : default;
            return new DistributedEntry<T>(value, header.Tags, TimeSpan.FromMilliseconds(header.ExpiresAt - now));
        }

        /// <summary>
        /// Writes an entry created at <paramref name="createdAt"/> (when its value was read from the source), so tag
        /// invalidations that happened while the value was loading make it stale.
        /// </summary>
        public async ValueTask SetAsync<T>(string key, T value, string[]? tags, long createdAt, TimeSpan lifetime, CancellationToken cancellationToken = default)
        {
            if (lifetime > _tagRetention)
            {
                lifetime = _tagRetention;
            }

            var lifetimeMilliseconds = (long)lifetime.TotalMilliseconds;
            if (lifetimeMilliseconds <= 0)
            {
                return;
            }

            var buffer = new ArrayBufferWriter<byte>(256);
            var hasValue = value is not null;
            WriteHeader(buffer, _contentType, hasValue, createdAt, Now + lifetimeMilliseconds, tags);
            if (hasValue)
            {
                _serializer.Serialize(value, buffer);
            }

            var entryOptions = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMilliseconds(lifetimeMilliseconds) };
            if (_bufferCache != null)
            {
                await _bufferCache.SetAsync(_entryPrefix + key, new ReadOnlySequence<byte>(buffer.WrittenMemory), entryOptions, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _cache.SetAsync(_entryPrefix + key, buffer.WrittenSpan.ToArray(), entryOptions, cancellationToken).ConfigureAwait(false);
            }

            // Other nodes may hold an older value in memory
            if (_backplane != null)
            {
                await _backplane.PublishInvalidationAsync(key, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task RemoveAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
        {
            foreach (var key in keys)
            {
                await _cache.RemoveAsync(_entryPrefix + key, cancellationToken).ConfigureAwait(false);
                if (_backplane != null)
                {
                    await _backplane.PublishInvalidationAsync(key, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Records that the tags were invalidated now, so every entry created before carrying them counts as missing.
        /// </summary>
        public async Task InvalidateTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
        {
            var now = Now;
            var stamp = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(stamp, now);
            var entryOptions = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = _tagRetention };

            foreach (var tag in tags)
            {
                await _cache.SetAsync(_tagPrefix + tag, stamp, entryOptions, cancellationToken).ConfigureAwait(false);
                RememberStamp(tag, new TagStamp(now, now));

                if (_backplane != null)
                {
                    await _backplane.PublishTagInvalidationAsync(tag, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Makes every entry written so far count as missing. The distributed cache cannot enumerate keys, so this
        /// invalidates a tag that every entry implicitly carries.
        /// </summary>
        public Task ClearAsync(CancellationToken cancellationToken = default)
            => InvalidateTagsAsync(new[] { ClearTag }, cancellationToken);

        private async ValueTask<bool> IsInvalidatedAsync(long createdAt, string[]? tags, CancellationToken cancellationToken)
        {
            if (await GetStampAsync(ClearTag, cancellationToken).ConfigureAwait(false) >= createdAt)
            {
                return true;
            }

            if (tags != null)
            {
                foreach (var tag in tags)
                {
                    if (await GetStampAsync(tag, cancellationToken).ConfigureAwait(false) >= createdAt)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private async ValueTask<long> GetStampAsync(string tag, CancellationToken cancellationToken)
        {
            var now = Now;
            if (_tagStamps.TryGetValue(tag, out var known) && now - known.CheckedAt < _tagCheckMilliseconds)
            {
                return known.InvalidatedAt;
            }

            var bytes = await _cache.GetAsync(_tagPrefix + tag, cancellationToken).ConfigureAwait(false);
            var invalidatedAt = bytes is { Length: >= sizeof(long) } ? BinaryPrimitives.ReadInt64LittleEndian(bytes) : long.MinValue;
            RememberStamp(tag, new TagStamp(invalidatedAt, now));
            return invalidatedAt;
        }

        private void RememberStamp(string tag, TagStamp stamp)
        {
            // Tags can be per-entity ("user:42"), so keep the local copy bounded; forgotten stamps are re-read
            if (_tagStamps.Count >= MaxTrackedTags)
            {
                _tagStamps.Clear();
            }

            _tagStamps[tag] = stamp;
        }

        private async Task SubscribeAsync(IBackplane backplane)
        {
            try
            {
                await backplane.SubscribeAsync(OnBackplaneMessageAsync).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _onBackplaneError?.Invoke(ex);
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
                _onBackplaneError?.Invoke(ex);
            }

            return Task.CompletedTask;
        }

        // Layout: version, has-value flag, created (Unix ms), expires (Unix ms), tag count, then each tag as a
        // length-prefixed UTF-8 string, then the serialized value.
        private static void WriteHeader(IBufferWriter<byte> writer, byte[] contentType, bool hasValue, long createdAt, long expiresAt, string[]? tags)
        {
            var span = writer.GetSpan(HeaderSize);
            span[0] = FormatVersion;
            span[1] = hasValue ? (byte)1 : (byte)0;
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
                !reader.TryRead(out var hasValue) ||
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

            header = new Header(hasValue == 1, createdAt, expiresAt, tags);
            payload = reader.UnreadSequence;
            return true;
        }

        private readonly record struct Header(bool HasValue, long CreatedAt, long ExpiresAt, string[]? Tags);

        private readonly record struct TagStamp(long InvalidatedAt, long CheckedAt);
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
