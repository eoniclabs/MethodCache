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
    private readonly IPolicyRegistry? _policies;

    public InMemoryCacheAdministrator(InMemoryCacheManager cache, IPolicyRegistry? policies)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _policies = policies;
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

        return result;
    }

    public CacheEntryInfo? GetEntry(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var engine = _cache.Engine;
        var now = engine.Now;
        return engine.TryPeek(key, out var node) && IsLive(node, now) ? Describe(node, now) : null;
    }

    public async ValueTask<bool> RemoveEntryAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        // An expired entry still in the map is removed too, but it was not cached, so it reports false like GetEntry.
        // With a distributed cache the key may live there or on another instance, which this one can't see; the
        // removal reached them, so it reports true.
        var engine = _cache.Engine;
        var wasLive = engine.TryPeek(key, out var node) && IsLive(node, engine.Now);
        var removed = await _cache.RemoveAsync(key).ConfigureAwait(false);
        return (removed && wasLive) || _cache.HasDistributedCache;
    }

    public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        cancellationToken.ThrowIfCancellationRequested();
        return _cache.InvalidateByTagsAsync(tags.ToArray());
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _cache.ClearAsync();
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
