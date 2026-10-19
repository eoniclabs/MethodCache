namespace MethodCache
{
    /// <summary>
    /// An immutable snapshot of statistics observed by this container; it does not inventory the shared backend.
    /// </summary>
    public sealed record CacheStats
    {
        /// <summary>Total number of cache hits.</summary>
        public long Hits { get; init; }
        /// <summary>Total number of cache misses.</summary>
        public long Misses { get; init; }
        /// <summary>Total number of evictions.</summary>
        public long Evictions { get; init; }
        /// <summary>Number of locally stored method results.</summary>
        public long LocalMethodEntries { get; init; }
        /// <summary>
        /// Total weight of locally stored method results, in the application weigher's units (one per entry by default).
        /// </summary>
        public long TotalWeight { get; init; }
        /// <summary>Number of locally stored HTTP representations.</summary>
        public long LocalHttpEntries { get; init; }
        /// <summary>Conservative storage cost of local HTTP representations, in bytes.</summary>
        public long LocalHttpBytes { get; init; }
        /// <summary>Bounded local observations of L2 HTTP representations; not resident or global entries.</summary>
        public long ObservedDistributedEntries { get; init; }
    }
}
