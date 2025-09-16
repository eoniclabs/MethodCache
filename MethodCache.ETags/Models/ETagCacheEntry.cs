namespace MethodCache.ETags.Models
{
    /// <summary>
    /// Represents a cache entry with ETag and metadata support.
    /// </summary>
    /// <typeparam name="T">The type of the cached value</typeparam>
    public class ETagCacheEntry<T>
    {
        /// <summary>
        /// The cached value.
        /// </summary>
        public T? Value { get; set; }

        /// <summary>
        /// The ETag for this cache entry.
        /// </summary>
        public string ETag { get; set; } = string.Empty;

        /// <summary>
        /// Timestamp when this entry was last modified.
        /// </summary>
        public DateTime LastModified { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Additional metadata associated with this cache entry.
        /// </summary>
        public Dictionary<string, string> Metadata { get; set; } = new();

        /// <summary>
        /// Indicates whether this entry represents a "not modified" state.
        /// Used for ETag conditional requests.
        /// </summary>
        public bool IsNotModified { get; set; }

        /// <summary>
        /// Creates a cache entry with a value and ETag.
        /// </summary>
        /// <param name="value">The value to cache</param>
        /// <param name="etag">The ETag for this entry</param>
        /// <returns>A new cache entry</returns>
        public static ETagCacheEntry<T> WithValue(T value, string etag)
        {
            return new ETagCacheEntry<T>
            {
                Value = value,
                ETag = etag,
                LastModified = DateTime.UtcNow,
                IsNotModified = false
            };
        }

        /// <summary>
        /// Creates a "not modified" cache entry.
        /// Used when the ETag hasn't changed.
        /// </summary>
        /// <param name="etag">The unchanged ETag</param>
        /// <returns>A not modified cache entry</returns>
        public static ETagCacheEntry<T> NotModified(string etag)
        {
            return new ETagCacheEntry<T>
            {
                Value = default,
                ETag = etag,
                LastModified = DateTime.UtcNow,
                IsNotModified = true
            };
        }

        /// <summary>
        /// Adds metadata to this cache entry.
        /// </summary>
        /// <param name="key">Metadata key</param>
        /// <param name="value">Metadata value</param>
        /// <returns>This cache entry for method chaining</returns>
        public ETagCacheEntry<T> WithMetadata(string key, string value)
        {
            Metadata[key] = value;
            return this;
        }

        /// <summary>
        /// Sets the last modified timestamp.
        /// </summary>
        /// <param name="timestamp">The last modified timestamp</param>
        /// <returns>This cache entry for method chaining</returns>
        public ETagCacheEntry<T> WithLastModified(DateTime timestamp)
        {
            LastModified = timestamp;
            return this;
        }
    }
}