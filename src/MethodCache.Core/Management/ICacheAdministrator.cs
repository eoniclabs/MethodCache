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
    ValueTask<CacheStats> GetStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists live entries, optionally filtered by a prefix of <see cref="CacheEntryInfo.StoredKey"/>, by tag and by the
    /// method they belong to. The result is a snapshot ordered by stored key and capped at <paramref name="limit"/>
    /// entries.
    /// </summary>
    /// <param name="storedKeyPrefix">Only entries whose <see cref="CacheEntryInfo.StoredKey"/> starts with this text.</param>
    /// <param name="tag">Only entries carrying this tag.</param>
    /// <param name="limit">The most entries to return.</param>
    /// <param name="methodId">
    /// Only the results of this cached method, by exactly its <see cref="CachedMethodInfo.MethodId"/>, as
    /// <see cref="GetMethods"/> lists it and <see cref="IMethodCacheKeys.MethodId{TService}"/> returns it: all its
    /// arguments, versions, generic arguments and keyed registrations, and no other method's entries.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is zero or negative.</exception>
    IReadOnlyList<CacheEntryInfo> ListEntries(string? storedKeyPrefix = null, string? tag = null, int limit = 100, string? methodId = null);

    /// <summary>
    /// Describes one live entry by its <see cref="CacheEntryInfo.StoredKey"/>, or returns <c>null</c> when it is not
    /// cached.
    /// </summary>
    CacheEntryInfo? GetEntry(string storedKey);

    /// <summary>
    /// Removes one entry by its <see cref="CacheEntryInfo.StoredKey"/>, from memory and, when one is configured, from
    /// the distributed cache and the other instances. Returns <c>false</c> when the key was not cached in this
    /// instance's memory and there is no distributed cache that could hold it. Throws
    /// <see cref="CacheInvalidationQueuedException"/> while the distributed removal is queued for recovery.
    /// </summary>
    ValueTask<bool> RemoveEntryAsync(string storedKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every method result and HTTP response carrying any of <paramref name="tags"/>, also when the
    /// application replaced the method cache manager. Unlike <see cref="ICacheManager.InvalidateTagsAsync"/>, it
    /// completes only once a configured distributed cache confirmed the invalidation, and throws
    /// <see cref="CacheInvalidationQueuedException"/> while the invalidation is queued for recovery.
    /// </summary>
    ValueTask<InvalidationResult> InvalidateTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every result of one cached method (all its arguments, versions, generic arguments and keyed
    /// registrations) from memory and, when one is configured, from the distributed cache and the other instances.
    /// Like <see cref="InvalidateTagsAsync"/>, it completes only once a configured distributed cache confirmed the
    /// invalidation, and throws <see cref="CacheInvalidationQueuedException"/> while the invalidation is queued for
    /// recovery.
    /// </summary>
    /// <param name="methodId">
    /// The method's <see cref="CachedMethodInfo.MethodId"/>, as <see cref="GetMethods"/> lists it and
    /// <see cref="IMethodCacheKeys.MethodId{TService}"/> returns it.
    /// </param>
    /// <param name="cancellationToken">Cancels this caller's wait; the invalidation itself is not undone.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="methodId"/> is null or whitespace, or no cached method has that id.
    /// </exception>
    /// <exception cref="NotSupportedException">The application's own <see cref="ICacheManager"/> holds the method results.</exception>
    ValueTask<InvalidationResult> InvalidateMethodAsync(string methodId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every entry and resets the hit and miss counters. Throws <see cref="CacheInvalidationQueuedException"/>
    /// while the distributed clear is queued for recovery. When the application registered its own
    /// <see cref="ICacheManager"/>, only HTTP responses are removed; clear the method results through that manager.
    /// </summary>
    ValueTask ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>Reports bounded local invalidation recovery queues.</summary>
    InvalidationQueues GetInvalidationQueues();

    /// <summary>The effective policy of every cached method, as resolved from attributes, configuration and overrides.</summary>
    IReadOnlyList<CachedMethodInfo> GetMethods();
}
