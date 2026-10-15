using MethodCache.Core.Runtime.Http;

namespace MethodCache.Core.Management;

// HTTP administration remains available with a NoOp/application-owned method manager, without resolving
// or allocating an unused built-in method store behind that manager's back.
internal sealed class HttpCacheAdministrator(HttpResponseStore store) : ICacheAdministrator
{
    /// <inheritdoc />
    public ValueTask<bool?> CheckDistributedCacheAsync(CancellationToken cancellationToken = default) => store.CheckDistributedAsync(cancellationToken);
    /// <inheritdoc />
    public ValueTask<ICacheStats> GetStatsAsync(CancellationToken cancellationToken = default)
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
    public IReadOnlyList<CacheEntryInfo> ListEntries(string? keyPrefix = null, string? tag = null, int limit = 100)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return store.DescribeEntries(keyPrefix, tag, limit);
    }
    /// <inheritdoc />
    public CacheEntryInfo? GetEntry(string key) => store.Find(key) is { } info ? store.Describe(info) : null;
    /// <inheritdoc />
    public ValueTask<bool> RemoveEntryAsync(string key, CancellationToken cancellationToken = default) => store.RemoveObservationAsync(key, cancellationToken);
    /// <inheritdoc />
    public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default)
        => store.InvalidateTagsAsync(tags, cancellationToken).AsTask();
    /// <inheritdoc />
    public ValueTask ClearAsync(CancellationToken cancellationToken = default) => store.ClearAsync(cancellationToken);
    /// <inheritdoc />
    public InvalidationQueues GetInvalidationQueues() => store.RecoveryQueues;
    /// <inheritdoc />
    public IReadOnlyList<CachedMethodInfo> GetMethods() => [];
}
