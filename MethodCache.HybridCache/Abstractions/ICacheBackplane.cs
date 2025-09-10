using System;
using System.Threading.Tasks;

namespace MethodCache.HybridCache.Abstractions
{
    /// <summary>
    /// Defines a backplane communication mechanism for distributed cache invalidation.
    /// </summary>
    public interface ICacheBackplane : IDisposable
    {
        /// <summary>
        /// Event raised when cache invalidation is received from another instance.
        /// </summary>
        event EventHandler<CacheInvalidationEventArgs>? InvalidationReceived;

        /// <summary>
        /// Publishes a cache invalidation event to all connected instances.
        /// </summary>
        /// <param name="tags">Tags to invalidate</param>
        /// <returns>A task representing the async operation</returns>
        Task PublishInvalidationAsync(params string[] tags);

        /// <summary>
        /// Publishes a cache invalidation event for specific keys.
        /// </summary>
        /// <param name="keys">Keys to invalidate</param>
        /// <returns>A task representing the async operation</returns>
        Task PublishKeyInvalidationAsync(params string[] keys);

        /// <summary>
        /// Starts listening for invalidation events from other instances.
        /// </summary>
        Task StartListeningAsync();

        /// <summary>
        /// Stops listening for invalidation events.
        /// </summary>
        Task StopListeningAsync();

        /// <summary>
        /// Gets whether the backplane is currently connected and operational.
        /// </summary>
        bool IsConnected { get; }
    }

    /// <summary>
    /// Event arguments for cache invalidation events.
    /// </summary>
    public class CacheInvalidationEventArgs : EventArgs
    {
        /// <summary>
        /// Tags that should be invalidated.
        /// </summary>
        public string[] Tags { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Specific keys that should be invalidated.
        /// </summary>
        public string[] Keys { get; init; } = Array.Empty<string>();

        /// <summary>
        /// The source instance ID that sent the invalidation.
        /// </summary>
        public string SourceInstanceId { get; init; } = string.Empty;

        /// <summary>
        /// Timestamp when the invalidation was sent.
        /// </summary>
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Type of invalidation.
        /// </summary>
        public InvalidationType Type { get; init; }
    }

    /// <summary>
    /// Types of cache invalidation.
    /// </summary>
    public enum InvalidationType
    {
        /// <summary>
        /// Invalidate by tags.
        /// </summary>
        ByTags,

        /// <summary>
        /// Invalidate by specific keys.
        /// </summary>
        ByKeys,

        /// <summary>
        /// Clear entire cache.
        /// </summary>
        ClearAll
    }
}