namespace MethodCache.Core.Storage.Coordination.Layers;

/// <summary>
/// Context passed through the storage pipeline, tracking operation metadata and layer interactions.
/// </summary>
public sealed class StorageContext
{
    /// <summary>
    /// Gets the unique identifier for this operation.
    /// </summary>
    public string OperationId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Gets the start time of this operation.
    /// </summary>
    public DateTimeOffset StartTime { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets metadata that can be attached during operation execution.
    /// </summary>
    public Dictionary<string, object> Metadata { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the set of layer IDs that had cache hits during this operation.
    /// </summary>
    public HashSet<string> LayersHit { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the set of layer IDs that had cache misses during this operation.
    /// </summary>
    public HashSet<string> LayersMissed { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the tags associated with this operation (for Set/Remove operations).
    /// </summary>
    public string[]? Tags { get; set; }

    /// <summary>
    /// Gets the elapsed time since the operation started.
    /// </summary>
    public TimeSpan Elapsed => DateTimeOffset.UtcNow - StartTime;
}
