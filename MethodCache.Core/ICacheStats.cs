using System;

namespace MethodCache.Core
{
    /// <summary>
    /// Statistics for cache operations.
    /// </summary>
    public interface ICacheStats
    {
        /// <summary>
        /// Total number of cache hits.
        /// </summary>
        long Hits { get; }

        /// <summary>
        /// Total number of cache misses.
        /// </summary>
        long Misses { get; }

        /// <summary>
        /// Total number of evictions.
        /// </summary>
        long Evictions { get; }

        /// <summary>
        /// Current number of entries in the cache.
        /// </summary>
        long Entries { get; }

        /// <summary>
        /// Approximate memory usage in bytes.
        /// </summary>
        long MemoryUsage { get; }

        /// <summary>
        /// Hit ratio (hits / (hits + misses)).
        /// </summary>
        double HitRatio { get; }
    }

    /// <summary>
    /// Default implementation of cache statistics.
    /// </summary>
    public class CacheStats : ICacheStats
    {
        /// <inheritdoc />
        public long Hits { get; init; }

        /// <inheritdoc />
        public long Misses { get; init; }

        /// <inheritdoc />
        public long Evictions { get; init; }

        /// <inheritdoc />
        public long Entries { get; init; }

        /// <inheritdoc />
        public long MemoryUsage { get; init; }

        /// <inheritdoc />
        public double HitRatio => Hits + Misses > 0 ? (double)Hits / (Hits + Misses) : 0;
    }
}