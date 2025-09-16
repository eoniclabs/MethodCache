namespace MethodCache.ETags.Models
{
    /// <summary>
    /// Represents the result of an ETag-aware cache operation.
    /// </summary>
    /// <typeparam name="T">The type of the cached value</typeparam>
    public class ETagCacheResult<T>
    {
        /// <summary>
        /// The cached value (null for NotModified status).
        /// </summary>
        public T? Value { get; }

        /// <summary>
        /// The ETag associated with this result.
        /// </summary>
        public string ETag { get; }

        /// <summary>
        /// The cache operation status.
        /// </summary>
        public ETagCacheStatus Status { get; }

        /// <summary>
        /// Timestamp when this entry was last modified.
        /// </summary>
        public DateTime LastModified { get; }

        /// <summary>
        /// Additional metadata from the cache entry.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; }

        private ETagCacheResult(T? value, string etag, ETagCacheStatus status, DateTime lastModified, IReadOnlyDictionary<string, string>? metadata = null)
        {
            Value = value;
            ETag = etag;
            Status = status;
            LastModified = lastModified;
            Metadata = metadata ?? new Dictionary<string, string>();
        }

        /// <summary>
        /// Creates a "not modified" result when the ETag matches.
        /// </summary>
        /// <param name="etag">The unchanged ETag</param>
        /// <param name="lastModified">When the entry was last modified</param>
        /// <returns>A not modified cache result</returns>
        public static ETagCacheResult<T> NotModified(string etag, DateTime? lastModified = null)
        {
            return new ETagCacheResult<T>(
                default,
                etag,
                ETagCacheStatus.NotModified,
                lastModified ?? DateTime.UtcNow);
        }

        /// <summary>
        /// Creates a cache hit result.
        /// </summary>
        /// <param name="value">The cached value</param>
        /// <param name="etag">The ETag</param>
        /// <param name="lastModified">When the entry was last modified</param>
        /// <param name="metadata">Additional metadata</param>
        /// <returns>A cache hit result</returns>
        public static ETagCacheResult<T> Hit(T value, string etag, DateTime? lastModified = null, IReadOnlyDictionary<string, string>? metadata = null)
        {
            return new ETagCacheResult<T>(
                value,
                etag,
                ETagCacheStatus.Hit,
                lastModified ?? DateTime.UtcNow,
                metadata);
        }

        /// <summary>
        /// Creates a cache miss result.
        /// </summary>
        /// <param name="value">The newly created value</param>
        /// <param name="etag">The new ETag</param>
        /// <param name="lastModified">When the entry was created</param>
        /// <param name="metadata">Additional metadata</param>
        /// <returns>A cache miss result</returns>
        public static ETagCacheResult<T> Miss(T value, string etag, DateTime? lastModified = null, IReadOnlyDictionary<string, string>? metadata = null)
        {
            return new ETagCacheResult<T>(
                value,
                etag,
                ETagCacheStatus.Miss,
                lastModified ?? DateTime.UtcNow,
                metadata);
        }

        /// <summary>
        /// Creates a bypass result when content should not be cached.
        /// </summary>
        /// <returns>A bypass cache result</returns>
        public static ETagCacheResult<T> Bypass()
        {
            return new ETagCacheResult<T>(
                default,
                string.Empty,
                ETagCacheStatus.Bypass,
                DateTime.UtcNow);
        }

        /// <summary>
        /// Creates a result from an ETagCacheEntry.
        /// </summary>
        /// <param name="entry">The cache entry</param>
        /// <param name="status">The cache status</param>
        /// <returns>A cache result</returns>
        public static ETagCacheResult<T> FromEntry(ETagCacheEntry<T> entry, ETagCacheStatus status)
        {
            if (entry.IsNotModified)
            {
                return NotModified(entry.ETag, entry.LastModified);
            }

            return status switch
            {
                ETagCacheStatus.Hit => Hit(entry.Value!, entry.ETag, entry.LastModified, entry.Metadata),
                ETagCacheStatus.Miss => Miss(entry.Value!, entry.ETag, entry.LastModified, entry.Metadata),
                ETagCacheStatus.Bypass => Bypass(),
                _ => throw new ArgumentException($"Invalid status {status} for non-modified entry", nameof(status))
            };
        }

        /// <summary>
        /// Indicates whether this result represents a successful operation (Hit or Miss).
        /// </summary>
        public bool IsSuccess => Status == ETagCacheStatus.Hit || Status == ETagCacheStatus.Miss;

        /// <summary>
        /// Indicates whether the client should receive a 304 Not Modified response.
        /// </summary>
        public bool ShouldReturn304 => Status == ETagCacheStatus.NotModified;

        /// <summary>
        /// Indicates whether caching should be bypassed for this response.
        /// </summary>
        public bool ShouldBypass => Status == ETagCacheStatus.Bypass;
    }

    /// <summary>
    /// Represents the status of an ETag cache operation.
    /// </summary>
    public enum ETagCacheStatus
    {
        /// <summary>
        /// The ETag matches the cached version (HTTP 304 Not Modified).
        /// </summary>
        NotModified,

        /// <summary>
        /// Cache hit - value retrieved from cache.
        /// </summary>
        Hit,

        /// <summary>
        /// Cache miss - value created by factory function.
        /// </summary>
        Miss,

        /// <summary>
        /// Bypass - response should not be cached (error, streaming content, etc.).
        /// </summary>
        Bypass
    }
}