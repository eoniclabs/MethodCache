namespace MethodCache.Core.Storage.Layers;

/// <summary>
/// Result from a storage layer operation, indicating whether the operation was handled
/// and whether pipeline execution should continue.
/// </summary>
/// <typeparam name="T">The type of value being stored/retrieved.</typeparam>
public readonly struct StorageLayerResult<T>
{
    /// <summary>
    /// Gets the value retrieved from the layer (if found).
    /// </summary>
    public T? Value { get; init; }

    /// <summary>
    /// Gets whether the value was found in this layer.
    /// </summary>
    public bool Found { get; init; }

    /// <summary>
    /// Gets whether pipeline execution should stop after this layer.
    /// True = stop pipeline, False = continue to next layer.
    /// </summary>
    public bool StopPropagation { get; init; }

    /// <summary>
    /// Creates a successful result with a value, stopping pipeline execution.
    /// </summary>
    public static StorageLayerResult<T> Hit(T value) => new()
    {
        Value = value,
        Found = true,
        StopPropagation = true
    };

    /// <summary>
    /// Creates a miss result, continuing pipeline execution.
    /// </summary>
    public static StorageLayerResult<T> Miss() => new()
    {
        Found = false,
        StopPropagation = false
    };

    /// <summary>
    /// Creates a result indicating the layer did not handle the operation, continuing pipeline execution.
    /// </summary>
    public static StorageLayerResult<T> NotHandled() => new()
    {
        Found = false,
        StopPropagation = false
    };

    /// <summary>
    /// Creates a result that stops the pipeline without returning a value (e.g., circuit breaker).
    /// </summary>
    public static StorageLayerResult<T> StopWithoutValue() => new()
    {
        Found = false,
        StopPropagation = true
    };
}
