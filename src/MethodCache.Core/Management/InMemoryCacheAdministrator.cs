using MethodCache.Abstractions.Registry;
using MethodCache.Core.Infrastructure;
using MethodCache.Core.Runtime.Execution;
using MethodCache.Core.Runtime.Memory;

namespace MethodCache.Core.Management;

/// <summary>Administers the default in-memory cache manager.</summary>
internal sealed class InMemoryCacheAdministrator : ICacheAdministrator
{
    private const long NanosPerTick = 100;

    // Durations saturate far in the future; anything beyond a century is reported as "does not expire".
    private static readonly long NoExpiryThreshold = TimeSpan.FromDays(36_500).Ticks * NanosPerTick;

    private readonly InMemoryCacheManager _cache;
    private readonly IPolicyRegistry? _policies;

    public InMemoryCacheAdministrator(InMemoryCacheManager cache, IPolicyRegistry? policies)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _policies = policies;
    }

    public bool? IsDistributedCacheAvailable => _cache.IsDistributedCacheAvailable;

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
        var matches = new List<Node>();

        foreach (var node in engine.Map.Values)
        {
            if (IsLive(node, now)
                && (keyPrefix == null || node.Key.StartsWith(keyPrefix, StringComparison.Ordinal))
                && (tag == null || HasTag(node, tag)))
            {
                matches.Add(node);
            }
        }

        matches.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));

        var count = Math.Min(limit, matches.Count);
        var result = new CacheEntryInfo[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = Describe(matches[i], now);
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
        var engine = _cache.Engine;
        var wasLive = engine.TryPeek(key, out var node) && IsLive(node, engine.Now);
        var removed = await _cache.RemoveAsync(key).ConfigureAwait(false);
        return removed && wasLive;
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

    public IReadOnlyList<CachedMethodInfo> GetMethods()
    {
        if (_policies == null)
        {
            return Array.Empty<CachedMethodInfo>();
        }

        return _policies.GetAllPolicies()
            .OrderBy(static result => result.MethodId, StringComparer.Ordinal)
            .Select(static result => new CachedMethodInfo(
                result.MethodId,
                result.Policy.Duration,
                result.Policy.Tags,
                result.Policy.Version,
                result.Contributions.Select(static c => c.SourceId).Distinct(StringComparer.Ordinal).ToArray()))
            .ToArray();
    }

    private static bool IsLive(Node node, long now) => node.IsAlive && !node.IsLogicallyExpired(now);

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
