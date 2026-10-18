namespace MethodCache;

/// <summary>
/// Inspects and changes the cache while the application runs: statistics, entries, invalidation and the
/// effective policy of each cached method. Intended for admin tooling; resolve it from dependency injection.
/// </summary>
public interface ICacheAdministrator
{
    /// <summary>
    /// Asks whether the distributed cache (Redis, SQL Server) answers, waiting at most its operation timeout.
    /// <c>false</c> while it is unreachable and MethodCache serves from memory only; <c>null</c> when no distributed
    /// cache is configured.
    /// </summary>
    ValueTask<bool?> CheckDistributedCacheAsync(CancellationToken cancellationToken = default);

    /// <summary>Hit, miss, eviction, entry and memory statistics.</summary>
    ValueTask<ICacheStats> GetStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists live entries, optionally filtered by key prefix and tag. The result is a snapshot ordered by key and
    /// capped at <paramref name="limit"/> entries.
    /// </summary>
    IReadOnlyList<CacheEntryInfo> ListEntries(string? keyPrefix = null, string? tag = null, int limit = 100);

    /// <summary>Describes one live entry, or returns <c>null</c> when the key is not cached.</summary>
    CacheEntryInfo? GetEntry(string key);

    /// <summary>
    /// Removes one entry, from memory and, when one is configured, from the distributed cache and the other
    /// instances. Returns <c>false</c> when the key was not cached in this instance's memory and there is no
    /// distributed cache that could hold it.
    /// </summary>
    ValueTask<bool> RemoveEntryAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Removes every entry carrying any of <paramref name="tags"/>.</summary>
    Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default);

    /// <summary>Removes every entry and resets the hit and miss counters.</summary>
    ValueTask ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>Reports bounded local invalidation recovery queues.</summary>
    InvalidationQueues GetInvalidationQueues() => new(0, 0);

    /// <summary>The effective policy of every cached method, as resolved from attributes, configuration and overrides.</summary>
    IReadOnlyList<CachedMethodInfo> GetMethods();
}
