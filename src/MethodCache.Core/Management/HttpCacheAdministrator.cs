using MethodCache.Extensibility;
using MethodCache.Core.Runtime.Http;

namespace MethodCache.Core.Management;

// HTTP administration remains available with a NoOp/application-owned method manager, without resolving
// or allocating an unused built-in method store behind that manager's back.
internal sealed class HttpCacheAdministrator(HttpResponseStore store) : ICacheAdministrator
{
    /// <inheritdoc />
    public ValueTask<bool?> CheckDistributedCacheAsync(CancellationToken cancellationToken = default) => store.CheckDistributedAsync(cancellationToken);
    /// <inheritdoc />
    public ValueTask<CacheStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var http = store.Observe();
        return new(new CacheStats
        {
            LocalHttpEntries = http.LocalHttpEntries,
            LocalHttpBytes = http.LocalHttpBytes,
            ObservedDistributedEntries = http.ObservedDistributedEntries
        });
    }
    /// <inheritdoc />
    public IReadOnlyList<CacheEntryInfo> ListEntries(string? storedKeyPrefix = null, string? tag = null, int limit = 100)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return store.DescribeEntries(storedKeyPrefix, tag, limit);
    }
    /// <inheritdoc />
    public CacheEntryInfo? GetEntry(string storedKey) => store.Find(storedKey) is { } info ? store.Describe(info) : null;
    /// <inheritdoc />
    public ValueTask<bool> RemoveEntryAsync(string storedKey, CancellationToken cancellationToken = default) => store.RemoveObservationAsync(storedKey, cancellationToken);
    /// <inheritdoc />
    public ValueTask<InvalidationResult> InvalidateTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
        => store.InvalidateTagsAsync(InMemoryCacheManager.ValidateTargets(tags), cancellationToken);
    /// <inheritdoc />
    public ValueTask ClearAsync(CancellationToken cancellationToken = default) => store.ClearAsync(cancellationToken);
    /// <inheritdoc />
    public InvalidationQueues GetInvalidationQueues() => store.RecoveryQueues;
    /// <inheritdoc />
    public IReadOnlyList<CachedMethodInfo> GetMethods() => [];
}
