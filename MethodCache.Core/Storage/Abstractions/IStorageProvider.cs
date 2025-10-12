using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MethodCache.Core.Storage.Abstractions;

/// <summary>
/// Defines a storage provider for distributed caching operations.
/// </summary>
public interface IStorageProvider
{
    /// <summary>
    /// Gets the name of this storage provider.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets a value from storage.
    /// </summary>
    ValueTask<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a value in storage with expiration.
    /// </summary>
    ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a value in storage with expiration and tags.
    /// </summary>
    ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a value from storage.
    /// </summary>
    ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all values associated with a tag.
    /// </summary>
    ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a key exists in storage.
    /// </summary>
    ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the health status of this storage provider.
    /// </summary>
    ValueTask<HealthStatus> GetHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets storage statistics if supported.
    /// </summary>
    ValueTask<StorageStats?> GetStatsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Statistics about storage provider performance.
/// </summary>
public class StorageStats
{
    /// <summary>
    /// Number of get operations.
    /// </summary>
    public long GetOperations { get; init; }

    /// <summary>
    /// Number of set operations.
    /// </summary>
    public long SetOperations { get; init; }

    /// <summary>
    /// Number of remove operations.
    /// </summary>
    public long RemoveOperations { get; init; }

    /// <summary>
    /// Average response time in milliseconds.
    /// </summary>
    public double AverageResponseTimeMs { get; init; }

    /// <summary>
    /// Number of errors encountered.
    /// </summary>
    public long ErrorCount { get; init; }

    /// <summary>
    /// Provider-specific statistics.
    /// </summary>
    public Dictionary<string, object> AdditionalStats { get; init; } = new();
}
