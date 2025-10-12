namespace MethodCache.Core.Storage.Abstractions;

/// <summary>
/// Defines a memory storage provider for L1 caching operations.
/// </summary>
public interface IMemoryStorage
{
    /// <summary>
    /// Gets a value from memory storage.
    /// </summary>
    T? Get<T>(string key);

    /// <summary>
    /// Gets a value from memory storage asynchronously.
    /// </summary>
    ValueTask<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a value in memory storage with expiration.
    /// </summary>
    void Set<T>(string key, T value, TimeSpan expiration);

    /// <summary>
    /// Sets a value in memory storage with expiration and tags.
    /// </summary>
    void Set<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags);

    /// <summary>
    /// Sets a value in memory storage asynchronously.
    /// </summary>
    ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a value in memory storage asynchronously with tags.
    /// </summary>
    ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a value from memory storage.
    /// </summary>
    void Remove(string key);

    /// <summary>
    /// Removes a value from memory storage asynchronously.
    /// </summary>
    ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all values associated with a tag.
    /// </summary>
    void RemoveByTag(string tag);

    /// <summary>
    /// Removes all values associated with a tag asynchronously.
    /// </summary>
    ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a key exists in memory storage.
    /// </summary>
    bool Exists(string key);

    /// <summary>
    /// Gets memory storage statistics.
    /// </summary>
    MemoryStorageStats GetStats();

    /// <summary>
    /// Clears all entries from memory storage.
    /// </summary>
    void Clear();
}

/// <summary>
/// Statistics about memory storage performance.
/// </summary>
public class MemoryStorageStats
{
    /// <summary>
    /// Number of entries currently in memory.
    /// </summary>
    public long EntryCount { get; init; }

    /// <summary>
    /// Number of cache hits.
    /// </summary>
    public long Hits { get; init; }

    /// <summary>
    /// Number of cache misses.
    /// </summary>
    public long Misses { get; init; }

    /// <summary>
    /// Number of evictions due to memory pressure.
    /// </summary>
    public long Evictions { get; init; }

    /// <summary>
    /// Hit ratio (hits / (hits + misses)).
    /// </summary>
    public double HitRatio => Hits + Misses > 0 ? (double)Hits / (Hits + Misses) : 0;

    /// <summary>
    /// Estimated memory usage in bytes.
    /// </summary>
    public long EstimatedMemoryUsage { get; init; }

    /// <summary>
    /// Number of tag-to-key mappings being tracked.
    /// </summary>
    public int TagMappingCount { get; init; }
}
