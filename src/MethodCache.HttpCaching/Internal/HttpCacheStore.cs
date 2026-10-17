using System.Runtime.CompilerServices;
using MethodCache.Core.Runtime.Http;

namespace MethodCache.HttpCaching.Internal;

// The add-on owns only protocol objects/encoding. Residency, epochs, owned I/O and administration live in Core.
internal sealed class HttpCacheStore(HttpResponseStore core, TimeProvider? timeProvider = null) : IHttpCacheStorage
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConditionalWeakTable<HttpCacheOptions, ClientViews> _views = new();
    private sealed class ClientViews
    {
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<string, View> Clients = new(StringComparer.Ordinal);
    }
    /// <inheritdoc />
    public IHttpCacheStorage ForClient(string client, HttpCacheOptions options)
        => _views.GetValue(options, static _ => new ClientViews()).Clients.GetOrAdd(client,
            static (name, state) => new View(state.Core, state.Time, name, state.Options), (Core: core, Time: _time, Options: options));

    // Direct use in tests/admin binds the unnamed client. Real handler invocations capture a client view once.
    private readonly HttpCacheOptions _default = new();
    private IHttpCacheStorage Default => ForClient(string.Empty, _default);
    /// <inheritdoc />
    public ValueTask<string> GetGenerationAsync(string key, CancellationToken cancellationToken = default) => Default.GetGenerationAsync(key, cancellationToken);
    /// <inheritdoc />
    public ValueTask InvalidateAsync(string key, CancellationToken cancellationToken = default) => Default.InvalidateAsync(key, cancellationToken);
    /// <inheritdoc />
    public ValueTask<HttpCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default) => Default.GetAsync(key, cancellationToken);
    /// <inheritdoc />
    public ValueTask SetAsync(string key, HttpCacheEntry entry, TimeSpan timeToLive, CancellationToken cancellationToken = default) => Default.SetAsync(key, entry, timeToLive, cancellationToken);
    /// <inheritdoc />
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) => Default.RemoveAsync(key, cancellationToken);
    /// <inheritdoc />
    public ValueTask ClearAsync(CancellationToken cancellationToken = default) => core.ClearAsync(cancellationToken);

    private sealed class View : IHttpCacheStorage
    {
        private readonly HttpResponseStore _core;
        private readonly TimeProvider _time;
        private readonly string _client;
        private readonly string[] _tags;
        private readonly HttpResponseStore.UrlSlotPrefix _slotPrefix;
        private readonly bool _defaultTags;
        private const string Unavailable = "|generation:unavailable:";
        internal View(HttpResponseStore core, TimeProvider time, string client, HttpCacheOptions options)
        {
            if (client.Length > 1024) throw new ArgumentException("HTTP cache client names cannot exceed 1024 UTF-16 code units.", nameof(client));
            _core = core; _time = time; _client = client;
            _slotPrefix = new(HttpCacheKeys.ClientIdentity(client) + ":");
            _defaultTags = options.Tags.Count == 0;
            if (options.Tags.Count > 125 || options.Tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 1024))
                throw new ArgumentException("HTTP static tags must be nonempty, at most 1024 code units each, with at most 125 custom tags.", nameof(options));
            _tags = options.Tags.Append("http:" + MethodCacheKeys.Part(client)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        /// <inheritdoc />
        public long BodyBudget(string key, HttpCacheEntry metadata)
            => _core.Distributed ? long.MaxValue : _core.MaximumBytes - EntryCost(key, metadata) - 256;
        private long EntryCost(string key, HttpCacheEntry entry, int? targetLength = null)
        {
            long metadata = 512L + 2L * (key.Length + (targetLength ?? TargetSpan(key).Length) + 69 + _client.Length);
            foreach (var tag in _tags) metadata += 48L + 2L * tag.Length;
            return Cost(entry) + metadata;
        }
        /// <inheritdoc />
        public ValueTask<string> GetGenerationAsync(string key, CancellationToken cancellationToken = default)
        {
            var slot = _slotPrefix.Slot(HttpCacheKeys.GenerationTargetSpan(key), out var canonical);
            var captured = _core.CaptureSlotAsync(slot, _tags, cancellationToken, allowInitialNamespace: canonical && _defaultTags);
            return captured.IsCompletedSuccessfully ? new(GenerationOrUnavailable(key, captured.Result)) : AwaitGenerationAsync(key, captured);
        }
        private async ValueTask<string> AwaitGenerationAsync(string key, ValueTask<string?> captured)
            => GenerationOrUnavailable(key, await captured.ConfigureAwait(false));
        private string GenerationOrUnavailable(string key, string? vector)
        {
            if (vector != null) return vector;
            _core.ForgetUrl(HttpCacheKeys.GenerationTarget(key));
            return "unavailable:" + Guid.NewGuid().ToString("N");
        }
        /// <inheritdoc />
        public ValueTask InvalidateAsync(string key, CancellationToken cancellationToken = default)
            => _core.InvalidateUrlsAsync([HttpCacheKeys.GenerationTarget(key)], cancellationToken);
        /// <inheritdoc />
        public ValueTask InvalidateManyAsync(IReadOnlyList<string> keys, CancellationToken token)
            => _core.InvalidateUrlsAsync(keys.Select(HttpCacheKeys.GenerationTarget), token);
        /// <inheritdoc />
        public ValueTask<HttpCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // TrySet rejects unpublishable generations, so local lookup cannot find one. Native mode
            // additionally avoids issuing I/O for such a generation after a failed metadata read.
            if (!_core.Distributed) return new(_core.ReadLocal(key) as HttpCacheEntry);
            return key.Contains(Unavailable, StringComparison.Ordinal) ? new((HttpCacheEntry?)null) : ReadDistributedAsync(key, cancellationToken);
        }
        private async ValueTask<HttpCacheEntry?> ReadDistributedAsync(string key, CancellationToken cancellationToken)
        {
            var data = await _core.ReadBytesAsync(key, cancellationToken).ConfigureAwait(false);
            var stored = data == null ? null : HttpCacheEntrySerializer.Deserialize(data);
            if (stored is not { } valid || valid.ExpiresAt <= _time.GetUtcNow())
            {
                _core.ForgetUrl(Target(key));
                return null;
            }
            _core.ObserveRepresentation(Info(key, valid.Entry, valid.ExpiresAt));
            return valid.Entry;
        }
        /// <inheritdoc />
        public async ValueTask SetAsync(string key, HttpCacheEntry entry, TimeSpan timeToLive, CancellationToken cancellationToken = default)
            => _ = await TrySetAsync(key, entry, timeToLive, cancellationToken).ConfigureAwait(false);
        /// <inheritdoc />
        public async ValueTask<bool> TrySetAsync(string key, HttpCacheEntry entry, TimeSpan lifetime, CancellationToken token)
        {
            if (lifetime <= TimeSpan.Zero || key.Contains(Unavailable, StringComparison.Ordinal)) return false;
            var deadline = _time.GetUtcNow() + lifetime;
            var info = Info(key, entry, deadline);
            if (!_core.Distributed) return _core.StoreLocal(key, entry, info);
            var data = HttpCacheEntrySerializer.Serialize(entry, deadline);
            if (!await _core.WriteBytesAsync(key, data, deadline, token).ConfigureAwait(false)) return false;
            _core.ObserveRepresentation(info);
            return true;
        }
        /// <inheritdoc />
        public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) => _core.RemoveAsync(key, cancellationToken);
        /// <inheritdoc />
        public ValueTask ClearAsync(CancellationToken cancellationToken = default) => _core.ClearAsync(cancellationToken);
        private HttpStorageInfo Info(string key, HttpCacheEntry entry, DateTimeOffset deadline)
        {
            if (!_core.Distributed && _core.FindLocalInfo(key) is { } previous && previous.Client == _client &&
                previous.Format == _core.Format && (ReferenceEquals(previous.Tags, _tags) || previous.Tags.SequenceEqual(_tags, StringComparer.Ordinal)))
                return previous with
                {
                    Cost = EntryCost(key, entry, previous.LocalTargetLength >= 0 ? previous.LocalTargetLength : previous.UrlTarget.Length),
                    CreatedAt = entry.StoredAt,
                    Deadline = deadline,
                    Selector = entry.VariantGeneration != null
                };
            if (!_core.Distributed)
            {
                var target = TargetSpan(key);
                // Local administration derives both texts from the immutable node key when inspected.
                // Keep charging their conservative cost before they are materialized.
                return new(string.Empty, string.Empty, _client, _core.Format, EntryCost(key, entry, target.Length),
                    _tags, entry.StoredAt, deadline, entry.VariantGeneration != null, target.Length, HttpResponseStore.Slot(target));
            }
            var distributedTarget = Target(key);
            return new(HttpResponseStore.Identity(key), distributedTarget, _client, _core.Format,
                EntryCost(key, entry, distributedTarget.Length), _tags, entry.StoredAt, deadline, entry.VariantGeneration != null);
        }
        private static string Target(string key)
        {
            var target = TargetSpan(key);
            return target.Length == key.Length ? key : new string(target);
        }
        private static ReadOnlySpan<char> TargetSpan(string key)
        {
            var generation = key.IndexOf("|generation:", StringComparison.Ordinal);
            var method = key.IndexOf(":m:", StringComparison.Ordinal);
            var end = generation < 0 ? method : method < 0 ? generation : Math.Min(generation, method);
            return end < 0 ? key.AsSpan() : key.AsSpan(0, end);
        }
    }

    internal static long Cost(HttpCacheEntry entry)
    {
        // Storage cost, not an exact CLR heap estimate: array/object metadata and UTF-16 strings are charged
        // conservatively, including selector metadata. No user serializer or payload getters are invoked.
        long cost = 384L + entry.Content.LongLength;
        static long Text(string? value) => value == null ? 0 : 32L + 2L * value.Length;
        cost += Text(entry.RequestUri) + Text(entry.Method) + Text(entry.ETag) + Text(entry.VariantGeneration);
        static long HeaderCost(Dictionary<string, string[]> headers)
        {
            long result = 0;
            foreach (var header in headers)
            {
                result += 64 + Text(header.Key) + 24L + 8L * header.Value.Length;
                foreach (var value in header.Value) result += Text(value);
            }
            return result;
        }
        cost += HeaderCost(entry.Headers) + HeaderCost(entry.ContentHeaders);
        if (entry.VaryHeaders != null)
        {
            cost += 24 + 8L * entry.VaryHeaders.Length;
            foreach (var value in entry.VaryHeaders) cost += Text(value);
        }
        return cost;
    }
}
