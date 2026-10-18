using MethodCache.Core.Runtime.Http;
using MethodCache.Infrastructure;
using MethodCache.Core.Runtime.Memory;
using MethodCache.Extensibility;
using MethodCache.Policies;

namespace MethodCache.Core.Management;

/// <summary>Administers the default in-memory cache manager.</summary>
internal sealed class InMemoryCacheAdministrator : ICacheAdministrator
{
    private const long NanosPerTick = 100;

    // Durations saturate far in the future; anything beyond a century is reported as "does not expire".
    private static readonly long NoExpiryThreshold = TimeSpan.FromDays(36_500).Ticks * NanosPerTick;
    private static readonly IComparer<string> DescendingKeys = Comparer<string>.Create(
        static (a, b) => string.CompareOrdinal(b, a));

    private readonly InMemoryCacheManager _cache;
    private readonly HttpResponseStore? _http;
    private readonly IPolicyRegistry? _policies;

    public InMemoryCacheAdministrator(InMemoryCacheManager cache, IPolicyRegistry? policies, HttpResponseStore? http = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _policies = policies;
        _http = http;
    }

    public ValueTask<bool?> CheckDistributedCacheAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _cache.CheckDistributedCacheAsync(cancellationToken);
    }

    public ValueTask<ICacheStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _cache.GetStatsAsync();
    }

    public IReadOnlyList<CacheEntryInfo> ListEntries(string? keyPrefix = null, string? tag = null, int limit = 100)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var engine = _cache.Engine;
        var now = engine.Now;
        // A max heap retains only the smallest requested keys. Enumerate the map directly: Values would
        // allocate a snapshot of every entry and acquire all of the dictionary's locks.
        var matches = new PriorityQueue<Node, string>(Math.Min(limit, 256), DescendingKeys);

        foreach (var pair in engine.Map)
        {
            var node = pair.Value;
            if (IsLive(node, now)
                && (keyPrefix == null || node.Key.StartsWith(keyPrefix, StringComparison.Ordinal))
                && (tag == null || HasTag(node, tag)))
            {
                if (matches.Count < limit)
                {
                    matches.Enqueue(node, node.Key);
                }
                else if (matches.TryPeek(out _, out var largestKey) && string.CompareOrdinal(node.Key, largestKey) < 0)
                {
                    matches.DequeueEnqueue(node, node.Key);
                }
            }
        }

        var count = matches.Count;
        var result = new CacheEntryInfo[count];
        for (var i = count - 1; i >= 0; i--)
        {
            result[i] = Describe(matches.Dequeue(), now);
        }

        return _http == null ? result : result.Concat(_http.DescribeEntries(keyPrefix, tag, limit))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal).Take(limit).ToArray();
    }

    public CacheEntryInfo? GetEntry(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var engine = _cache.Engine;
        var now = engine.Now;
        return engine.TryPeek(key, out var node) && IsLive(node, now) ? Describe(node, now)
            : _http?.Find(key) is { } http ? _http.Describe(http) : null;
    }

    public async ValueTask<bool> RemoveEntryAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        var engine = _cache.Engine;
        var wasLive = engine.TryPeek(key, out var node) && IsLive(node, engine.Now);
        // Match GetEntry's precedence when an explicit method key resembles a hashed HTTP id.
        if (!wasLive && HttpResponseStore.IsObservationId(key) && _http != null &&
            await _http.RemoveObservationAsync(key, cancellationToken).ConfigureAwait(false)) return true;

        // With L2 an unobserved key can still live on another instance, so confirmed invalidation reports true.
        var result = await _cache.InvalidatePreparedKeysAsync([key], cancellationToken).ConfigureAwait(false);
        HttpResponseStore.RequireConfirmed(result);
        return wasLive || _cache.HasDistributedCache;
    }

    public async Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var result = await _cache.InvalidateTagsAsync(tags, cancellationToken).ConfigureAwait(false);
        HttpResponseStore.RequireConfirmed(result);
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _cache.ClearAsync().ConfigureAwait(false);
        if (_cache.GetInvalidationQueues().Distributed != 0)
            throw new CacheInvalidationUnavailableException("Cache clear is queued for distributed recovery.");
    }

    public InvalidationQueues GetInvalidationQueues() => _cache.GetInvalidationQueues();

    public IReadOnlyList<CachedMethodInfo> GetMethods()
    {
        if (_policies == null)
        {
            return Array.Empty<CachedMethodInfo>();
        }

        return _policies.GetAllPolicies()
            .OrderBy(static result => result.MethodId, StringComparer.Ordinal)
            .Select(result => new CachedMethodInfo(
                result.MethodId,
                result.Policy.Duration,
                result.Policy.Tags,
                result.Policy.Version,
                result.Contributions.Select(static c => c.SourceId).Distinct(StringComparer.Ordinal).ToArray())
            {
                Options = CacheEntryOptions.FromRuntime(CacheRuntimePolicy.FromResolverResult(result), preparedKey: false),
                L2Namespace = PolicyHandle.For(_policies, result.MethodId).Current.PrivateIdentity ? "private" : "shared",
                SettingSources = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                    new Dictionary<string, string>(result.FieldSources, StringComparer.Ordinal))
            })
            .ToArray();
    }

    private static bool IsLive(Node node, long now) => node.IsAlive && !node.IsLogicallyExpired(now) && !node.IsPhysicallyExpired(now);

    private static bool HasTag(Node node, string tag)
    {
        var tags = node.Tags;
        return tags != null && Array.IndexOf(tags, tag) >= 0;
    }

    private static CacheEntryInfo Describe(Node node, long now)
    {
        var remaining = Volatile.Read(ref node.LogicalExpiresAt) - now;

        return new CacheEntryInfo(
            node.Key,
            node.BoxedValue?.GetType().FullName,
            TimeSpan.FromTicks(Math.Max(0, now - node.CreatedAt) / NanosPerTick),
            remaining >= NoExpiryThreshold ? null : TimeSpan.FromTicks(remaining / NanosPerTick),
            node.Tags?.ToArray() ?? Array.Empty<string>(),
            node.Weight);
    }
}
