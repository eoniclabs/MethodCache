namespace MethodCache.Infrastructure.Abstractions;

/// <summary>
/// Defines a backplane for coordinating cache invalidation across multiple instances.
/// </summary>
public interface IBackplane
{
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
}

/// <summary>
/// Represents a backplane message for cache invalidation.
/// </summary>
public class BackplaneMessage
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
public enum BackplaneMessageType
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
    ClearAll
}