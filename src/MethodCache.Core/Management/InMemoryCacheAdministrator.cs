using MethodCache.Core.Runtime.Http;
using MethodCache.Infrastructure;
using MethodCache.Core.Runtime.Memory;
using MethodCache.Extensibility;
using MethodCache.Policies;
using MethodCache.Core.PolicyPipeline.Model;

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

    public ValueTask<CacheStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _cache.GetStatsAsync();
    }

    public IReadOnlyList<CacheEntryInfo> ListEntries(string? storedKeyPrefix = null, string? tag = null, int limit = 100, string? methodId = null)
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
                && (storedKeyPrefix == null || node.Key.StartsWith(storedKeyPrefix, StringComparison.Ordinal))
                && (tag == null || HasTag(node, tag))
                && (methodId == null || InMemoryCacheManager.MethodIdOf(node) == methodId))
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

        // HTTP responses belong to no method
        return _http == null || methodId != null ? result : result.Concat(_http.DescribeEntries(storedKeyPrefix, tag, limit))
            .OrderBy(entry => entry.StoredKey, StringComparer.Ordinal).Take(limit).ToArray();
    }

    public CacheEntryInfo? GetEntry(string storedKey)
    {
        ArgumentNullException.ThrowIfNull(storedKey);

        var engine = _cache.Engine;
        var now = engine.Now;
        return engine.TryPeek(storedKey, out var node) && IsLive(node, now) ? Describe(node, now)
            : _http?.Find(storedKey) is { } http ? _http.Describe(http) : null;
    }

    public async ValueTask<bool> RemoveEntryAsync(string storedKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storedKey);
        cancellationToken.ThrowIfCancellationRequested();
        var engine = _cache.Engine;
        var wasLive = engine.TryPeek(storedKey, out var node) && IsLive(node, engine.Now);
        // Match GetEntry's precedence when an explicit method key resembles a hashed HTTP id.
        if (!wasLive && HttpResponseStore.IsObservationId(storedKey) && _http != null &&
            await _http.RemoveObservationAsync(storedKey, cancellationToken).ConfigureAwait(false)) return true;

        // With L2 an unobserved key can still live on another instance, so confirmed invalidation reports true.
        var result = await _cache.InvalidatePreparedKeysAsync([storedKey], cancellationToken).ConfigureAwait(false);
        CacheInvalidationQueuedException.ThrowIfQueued(result);
        return wasLive || _cache.HasDistributedCache;
    }

    public async ValueTask<InvalidationResult> InvalidateTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var result = await _cache.InvalidateTagsAsync(tags, cancellationToken).ConfigureAwait(false);
        CacheInvalidationQueuedException.ThrowIfQueued(result);
        return result;
    }

    public async ValueTask<InvalidationResult> InvalidateMethodAsync(string methodId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(methodId)) throw new ArgumentException("A method id cannot be null or whitespace.", nameof(methodId));
        cancellationToken.ThrowIfCancellationRequested();
        // A mistyped id would remove nothing and still report success
        if (_policies != null && (MethodIds.IsDefaults(methodId) || !_policies.GetAllPolicies().Any(policy => policy.MethodId == methodId)))
        {
            throw new ArgumentException(
                $"No cached method has the id '{methodId}'. ICacheAdministrator.GetMethods() lists the ids, and " +
                "IMethodCacheKeys.MethodId returns the id of a call.", nameof(methodId));
        }

        // Once memory is cleared, distributed recovery belongs to the cache, not to the caller's token
        var invalidation = _cache.InvalidateMethodAsync(methodId);
        _ = invalidation.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var result = await invalidation.WaitAsync(cancellationToken).ConfigureAwait(false);
        CacheInvalidationQueuedException.ThrowIfQueued(result);
        return result;
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _cache.ClearAsync().ConfigureAwait(false);
        if (_cache.GetInvalidationQueues().Queued != 0)
            throw new CacheInvalidationQueuedException("Cache clear is queued for distributed recovery.");
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
            .Select(result => new CachedMethodInfo
            {
                MethodId = result.MethodId,
                Options = CacheEntryOptions.FromRuntime(CacheRuntimePolicy.FromResolverResult(result), preparedKey: false),
                IsDistributedNamespaceShared = !PolicyHandle.For(_policies, result.MethodId).Current.PrivateIdentity,
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

        return new CacheEntryInfo
        {
            StoredKey = node.Key,
            MethodId = InMemoryCacheManager.MethodIdOf(node),
            ValueType = node.BoxedValue?.GetType().FullName,
            Age = TimeSpan.FromTicks(Math.Max(0, now - node.CreatedAt) / NanosPerTick),
            TimeToLive = remaining >= NoExpiryThreshold ? null : TimeSpan.FromTicks(remaining / NanosPerTick),
            Tags = node.Tags?.ToArray() ?? Array.Empty<string>(),
            Weight = node.Weight
        };
    }
}
