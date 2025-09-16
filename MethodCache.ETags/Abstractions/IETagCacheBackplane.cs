using MethodCache.HybridCache.Abstractions;

namespace MethodCache.ETags.Abstractions
{
    /// <summary>
    /// Extends the standard cache backplane with ETag-specific invalidation capabilities.
    /// Enables cross-instance ETag consistency in distributed scenarios.
    /// </summary>
    public interface IETagCacheBackplane : ICacheBackplane
    {
        /// <summary>
        /// Raised when an ETag invalidation message is received from another instance.
        /// </summary>
        event EventHandler<ETagInvalidationEventArgs> ETagInvalidationReceived;

        /// <summary>
        /// Publishes an ETag invalidation message to all instances.
        /// </summary>
        /// <param name="key">The cache key that was invalidated</param>
        /// <param name="newETag">The new ETag value (optional)</param>
        Task PublishETagInvalidationAsync(string key, string? newETag = null);

        /// <summary>
        /// Publishes multiple ETag invalidations in a batch.
        /// </summary>
        /// <param name="invalidations">Collection of key-ETag pairs to invalidate</param>
        Task PublishETagInvalidationBatchAsync(IEnumerable<KeyValuePair<string, string?>> invalidations);
    }

    /// <summary>
    /// Event arguments for ETag invalidation events.
    /// </summary>
    public class ETagInvalidationEventArgs : EventArgs
    {
        /// <summary>
        /// The cache key that was invalidated.
        /// </summary>
        public string Key { get; }

        /// <summary>
        /// The new ETag value (null if completely invalidated).
        /// </summary>
        public string? NewETag { get; }

        /// <summary>
        /// Timestamp when the invalidation occurred.
        /// </summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// The instance ID that originated this invalidation.
        /// </summary>
        public string? OriginInstanceId { get; }

        public ETagInvalidationEventArgs(string key, string? newETag = null, DateTime? timestamp = null, string? originInstanceId = null)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key));
            NewETag = newETag;
            Timestamp = timestamp ?? DateTime.UtcNow;
            OriginInstanceId = originInstanceId;
        }
    }
}