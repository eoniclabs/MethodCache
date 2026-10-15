namespace MethodCache.Extensibility;

/// <summary>
/// Defines a backplane for coordinating cache invalidation across multiple instances.
/// </summary>
internal interface IBackplane
{
    /// <summary>Current delivery-connection capability; successful cache I/O does not imply this is connected.</summary>
    BackplaneState ConnectionState => BackplaneState.Unknown;

    /// <summary>
    /// Publishes a cache invalidation message.
    /// </summary>
    Task PublishInvalidationAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a tag-based cache invalidation message.
    /// </summary>
    Task PublishTagInvalidationAsync(string tag, CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes to cache invalidation messages.
    /// </summary>
    Task SubscribeAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unsubscribes from cache invalidation messages.
    /// </summary>
    Task UnsubscribeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the instance ID for this backplane.
    /// </summary>
    string InstanceId { get; }

    /// <summary>Publishes a declared-method invalidation without changing the receiver's policy.</summary>
    Task PublishMethodInvalidationAsync(string methodId, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("This backplane does not support method invalidation."));
    /// <summary>Publishes a format-independent clear operation.</summary>
    Task PublishClearAsync(CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("This backplane does not support clear invalidation."));

}

/// <summary>
/// Represents a backplane message for cache invalidation.
/// </summary>
internal class BackplaneMessage
{
    /// <summary>
    /// The type of invalidation.
    /// </summary>
    public BackplaneMessageType Type { get; init; }

    /// <summary>
    /// The cache key to invalidate (for key-based invalidation).
    /// </summary>
    public string? Key { get; init; }

    /// <summary>
    /// The tag to invalidate (for tag-based invalidation).
    /// </summary>
    public string? Tag { get; init; }

    /// <summary>
    /// The instance ID that sent this message.
    /// </summary>
    public string InstanceId { get; init; } = string.Empty;

    /// <summary>
    /// When this message was created.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Types of backplane messages.
/// </summary>
internal enum BackplaneMessageType
{
    /// <summary>
    /// Invalidate a specific cache key.
    /// </summary>
    KeyInvalidation,

    /// <summary>
    /// Invalidate all keys associated with a tag.
    /// </summary>
    TagInvalidation,

    /// <summary>
    /// Clear all cache entries.
    /// </summary>
    ClearAll,

    /// <summary>Invalidates a declared method across all registrations and policy identities.</summary>
    MethodInvalidation
}
internal enum BackplaneState { Unknown, NotConfigured, Connecting, Connected, Disconnected }
