namespace MethodCache.Core.Storage.Coordination.Layers;

/// <summary>
/// Represents a single layer in the storage pipeline with its own metrics and lifecycle.
/// Layers are composable and executed in priority order to form a complete storage strategy.
/// </summary>
public interface IStorageLayer : IAsyncDisposable
{
    /// <summary>
    /// Gets the unique identifier for this layer (e.g., "L1", "L2", "TagIndex").
    /// </summary>
    string LayerId { get; }

    /// <summary>
    /// Gets the priority of this layer (lower value = executed first).
    /// Typical values: L1=10, L2=20, L3=30, TagIndex=5, Backplane=100
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Gets whether this layer is currently enabled and should be included in the pipeline.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Initializes the layer asynchronously. Called once during coordinator startup.
    /// </summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles a Get operation for the specified key.
    /// </summary>
    /// <typeparam name="T">The type of value being retrieved.</typeparam>
    /// <param name="context">The operation context tracking the request.</param>
    /// <param name="key">The cache key to retrieve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A result indicating whether the value was found and whether to stop pipeline execution.
    /// Return Hit(value) to stop with a value, Miss() to continue to next layer.
    /// </returns>
    ValueTask<StorageLayerResult<T>> GetAsync<T>(
        StorageContext context,
        string key,
        CancellationToken cancellationToken);

    /// <summary>
    /// Handles a Set operation for the specified key.
    /// </summary>
    /// <typeparam name="T">The type of value being stored.</typeparam>
    /// <param name="context">The operation context tracking the request.</param>
    /// <param name="key">The cache key to set.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="expiration">The expiration timespan.</param>
    /// <param name="tags">Tags to associate with this entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask SetAsync<T>(
        StorageContext context,
        string key,
        T value,
        TimeSpan expiration,
        IEnumerable<string> tags,
        CancellationToken cancellationToken);

    /// <summary>
    /// Handles a Remove operation for the specified key.
    /// </summary>
    /// <param name="context">The operation context tracking the request.</param>
    /// <param name="key">The cache key to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask RemoveAsync(
        StorageContext context,
        string key,
        CancellationToken cancellationToken);

    /// <summary>
    /// Handles a RemoveByTag operation for the specified tag.
    /// </summary>
    /// <param name="context">The operation context tracking the request.</param>
    /// <param name="tag">The tag to invalidate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask RemoveByTagAsync(
        StorageContext context,
        string tag,
        CancellationToken cancellationToken);

    /// <summary>
    /// Checks if a key exists in this layer.
    /// </summary>
    /// <param name="context">The operation context tracking the request.</param>
    /// <param name="key">The cache key to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the key exists in this layer, false otherwise.</returns>
    ValueTask<bool> ExistsAsync(
        StorageContext context,
        string key,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets the health status of this layer.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Health status including any diagnostic information.</returns>
    ValueTask<LayerHealthStatus> GetHealthAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets statistics for this layer (hits, misses, operations, etc.).
    /// </summary>
    /// <returns>Layer statistics.</returns>
    LayerStats GetStats();
}
