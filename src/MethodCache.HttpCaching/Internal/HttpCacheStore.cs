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
        private const string Unavailable = "|generation:unavailable:";
        internal View(HttpResponseStore core, TimeProvider time, string client, HttpCacheOptions options)
        {
            if (client.Length > 1024) throw new ArgumentException("HTTP cache client names cannot exceed 1024 UTF-16 code units.", nameof(client));
            _core = core; _time = time; _client = client;
            if (options.Tags.Count > 125 || options.Tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 1024))
                throw new ArgumentException("HTTP static tags must be nonempty, at most 1024 code units each, with at most 125 custom tags.", nameof(options));
            _tags = options.Tags.Append("http:" + MethodCacheKeys.Part(client)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        /// <inheritdoc />
        public long BodyBudget(string key, HttpCacheEntry metadata)
            => _core.Distributed ? long.MaxValue : _core.MaximumBytes - EntryCost(key, metadata) - 256;
        private long EntryCost(string key, HttpCacheEntry entry)
        {
            long metadata = 512L + 2L * (key.Length + Target(key).Length + 69 + _client.Length);
            foreach (var tag in _tags) metadata += 48L + 2L * tag.Length;
            return Cost(entry) + metadata;
        }
        /// <inheritdoc />
        public async ValueTask<string> GetGenerationAsync(string key, CancellationToken cancellationToken = default)
        {
            var vector = await _core.CaptureAsync(HttpCacheKeys.GenerationTarget(key), _tags, cancellationToken).ConfigureAwait(false);
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
        public async ValueTask<HttpCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (key.Contains(Unavailable, StringComparison.Ordinal)) return null;
            if (!_core.Distributed) return _core.ReadLocal(key) as HttpCacheEntry;
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
            => new(HttpResponseStore.Identity(key), Target(key), _client, _core.Format, EntryCost(key, entry), _tags, entry.StoredAt, deadline, entry.VariantGeneration != null);
        private static string Target(string key)
        {
            var end = key.IndexOf("|generation:", StringComparison.Ordinal);
            return HttpCacheKeys.GenerationTarget(end < 0 ? key : key[..end]);
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
