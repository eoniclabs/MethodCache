namespace MethodCache
{
    /// <summary>
    /// Statistics observed by this container; these do not inventory the shared backend.
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
        /// Number of locally stored method results.
        /// </summary>
        long LocalMethodEntries { get; }

        /// <summary>
        /// Total weight of locally stored method results, in the application weigher's units (one per entry by default).
        /// </summary>
        long TotalWeight { get; }

        /// <summary>Number of locally stored HTTP representations.</summary>
        long LocalHttpEntries { get; }
        /// <summary>Conservative storage cost of local HTTP representations, in bytes.</summary>
        long LocalHttpBytes { get; }
        /// <summary>Bounded local observations of L2 HTTP representations; not resident or global entries.</summary>
        long ObservedDistributedEntries { get; }
    }

    /// <summary>
    /// An immutable snapshot of statistics from one container.
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
        public long LocalMethodEntries { get; init; }

        /// <inheritdoc />
        public long TotalWeight { get; init; }

        /// <inheritdoc />
        public long LocalHttpEntries { get; init; }
        /// <inheritdoc />
        public long LocalHttpBytes { get; init; }
        /// <inheritdoc />
        public long ObservedDistributedEntries { get; init; }
    }
}