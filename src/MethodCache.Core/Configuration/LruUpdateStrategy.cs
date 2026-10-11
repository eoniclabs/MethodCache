namespace MethodCache.Core.Configuration
{
    /// <summary>
    /// Defines the strategy for updating LRU (Least Recently Used) access order tracking.
    /// Different strategies offer different trade-offs between performance and accuracy.
    /// </summary>
    public enum LruUpdateStrategy
    {
        /// <summary>
        /// Probabilistic updates using Redis-style approximate LRU.
        /// Only updates access order for a small percentage of cache hits (default 1%).
        ///
        /// Performance: ~30µs cache hits
        /// Safety: Battle-tested, production-proven
        /// LRU Accuracy: ~95% (statistically approximate)
        /// Lock Contention: 99% reduction
        ///
        /// RECOMMENDED: Best balance of performance and safety for most use cases.
        /// </summary>
        Probabilistic = 0,

        /// <summary>
        /// Lock-free Clock-LRU algorithm using atomic operations.
        /// Updates access bits without acquiring locks, provides best performance.
        ///
        /// Performance: ~15-20µs cache hits
        /// Safety: Requires thorough testing on target platforms
        /// LRU Accuracy: ~90% (approximate)
        /// Lock Contention: None (fully lock-free)
        ///
        /// ADVANCED: Use only after extensive testing. Suitable for high-performance scenarios
        /// where every microsecond matters. Test thoroughly on all target platforms (ARM, x64).
        /// </summary>
        LockFree = 1,

        /// <summary>
        /// Precise LRU with locks acquired on every cache access.
        /// Updates access order on every hit, providing perfect LRU semantics.
        ///
        /// Performance: ~70-90µs cache hits (high lock contention)
        /// Safety: Safe but slow
        /// LRU Accuracy: 100% (perfect LRU)
        /// Lock Contention: High (every access acquires lock)
        ///
        /// LEGACY: Not recommended for production. Kept for compatibility, debugging,
        /// and accuracy comparison. Use only when perfect LRU semantics are required.
        /// </summary>
        Precise = 2
    }
}
