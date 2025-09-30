using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MethodCache.Core.Storage;

/// <summary>
/// Defines a persistent storage provider for long-term cache storage (L3 layer).
/// This layer provides durability and large storage capacity for cache data that should persist across application restarts.
/// </summary>
public interface IPersistentStorageProvider
{
    /// <summary>
    /// Gets the name of this persistent storage provider.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets a value from persistent storage.
    /// </summary>
    ValueTask<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a value in persistent storage with expiration.
    /// Persistent storage typically has longer expiration times compared to L1/L2 caches.
    /// </summary>
    ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a value in persistent storage with expiration and tags.
    /// </summary>
    ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a value from persistent storage.
    /// </summary>
    ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all values associated with a tag from persistent storage.
    /// </summary>
    ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a key exists in persistent storage.
    /// </summary>
    ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the health status of this persistent storage provider.
    /// </summary>
    ValueTask<HealthStatus> GetHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets persistent storage statistics if supported.
    /// </summary>
    ValueTask<PersistentStorageStats?> GetStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs cleanup operations on expired entries.
    /// This is important for persistent storage to manage disk space and performance.
    /// </summary>
    ValueTask CleanupExpiredEntriesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the total size of data stored in persistent storage (in bytes).
    /// </summary>
    ValueTask<long> GetStorageSizeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Statistics specific to persistent storage providers.
/// </summary>
public class PersistentStorageStats : StorageStats
{
    /// <summary>
    /// Total disk space used by the persistent cache (in bytes).
    /// </summary>
    public long DiskSpaceUsedBytes { get; init; }

    /// <summary>
    /// Number of entries currently stored.
    /// </summary>
    public long EntryCount { get; init; }

    /// <summary>
    /// Number of expired entries cleaned up.
    /// </summary>
    public long ExpiredEntriesCleaned { get; init; }

    /// <summary>
    /// Average time to persist an entry (in milliseconds).
    /// </summary>
    public double AveragePersistTimeMs { get; init; }

    /// <summary>
    /// Average time to retrieve an entry (in milliseconds).
    /// </summary>
    public double AverageRetrievalTimeMs { get; init; }

    /// <summary>
    /// Number of database connections currently active.
    /// </summary>
    public int ActiveConnections { get; init; }

    /// <summary>
    /// Last time cleanup was performed.
    /// </summary>
    public DateTime? LastCleanupTime { get; init; }
}
