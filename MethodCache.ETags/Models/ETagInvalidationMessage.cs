using System.Text.Json.Serialization;

namespace MethodCache.ETags.Models
{
    /// <summary>
    /// Represents an ETag invalidation message for cross-instance communication.
    /// </summary>
    public class ETagInvalidationMessage
    {
        /// <summary>
        /// The cache key to invalidate.
        /// </summary>
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// The new ETag value (null if completely invalidated).
        /// </summary>
        [JsonPropertyName("newETag")]
        public string? NewETag { get; set; }

        /// <summary>
        /// Timestamp when the invalidation occurred.
        /// </summary>
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// The instance ID that originated this invalidation.
        /// </summary>
        [JsonPropertyName("originInstanceId")]
        public string? OriginInstanceId { get; set; }

        /// <summary>
        /// Message type identifier for routing.
        /// </summary>
        [JsonPropertyName("messageType")]
        public string MessageType { get; set; } = "ETagInvalidation";

        /// <summary>
        /// Batch of invalidations (for batch operations).
        /// </summary>
        [JsonPropertyName("batch")]
        public List<ETagInvalidationItem>? Batch { get; set; }

        /// <summary>
        /// Creates a single invalidation message.
        /// </summary>
        /// <param name="key">Cache key</param>
        /// <param name="newETag">New ETag value</param>
        /// <param name="originInstanceId">Origin instance ID</param>
        /// <returns>Invalidation message</returns>
        public static ETagInvalidationMessage Create(string key, string? newETag = null, string? originInstanceId = null)
        {
            return new ETagInvalidationMessage
            {
                Key = key,
                NewETag = newETag,
                OriginInstanceId = originInstanceId,
                Timestamp = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Creates a batch invalidation message.
        /// </summary>
        /// <param name="invalidations">Collection of key-ETag pairs</param>
        /// <param name="originInstanceId">Origin instance ID</param>
        /// <returns>Batch invalidation message</returns>
        public static ETagInvalidationMessage CreateBatch(IEnumerable<KeyValuePair<string, string?>> invalidations, string? originInstanceId = null)
        {
            return new ETagInvalidationMessage
            {
                MessageType = "ETagInvalidationBatch",
                Batch = invalidations.Select(kvp => new ETagInvalidationItem { Key = kvp.Key, NewETag = kvp.Value }).ToList(),
                OriginInstanceId = originInstanceId,
                Timestamp = DateTime.UtcNow
            };
        }
    }

    /// <summary>
    /// Represents a single item in a batch ETag invalidation.
    /// </summary>
    public class ETagInvalidationItem
    {
        /// <summary>
        /// The cache key to invalidate.
        /// </summary>
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// The new ETag value (null if completely invalidated).
        /// </summary>
        [JsonPropertyName("newETag")]
        public string? NewETag { get; set; }
    }
}