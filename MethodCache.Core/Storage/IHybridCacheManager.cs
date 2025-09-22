using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MethodCache.Core.Storage
{
    /// <summary>
    /// Defines a hybrid cache manager that coordinates L1 (in-memory), L2 (distributed), and L3 (persistent) caching.
    /// </summary>
    public interface IHybridCacheManager : ICacheManager
    {
        /// <summary>
        /// Gets a value from the L1 cache only.
        /// </summary>
        Task<T?> GetFromL1Async<T>(string key);

        /// <summary>
        /// Gets a value from the L2 cache only.
        /// </summary>
        Task<T?> GetFromL2Async<T>(string key);

        /// <summary>
        /// Gets a value from the L3 cache only.
        /// </summary>
        Task<T?> GetFromL3Async<T>(string key);

        /// <summary>
        /// Sets a value in the L1 cache only.
        /// </summary>
        Task SetInL1Async<T>(string key, T value, TimeSpan expiration);

        /// <summary>
        /// Sets a value in the L1 cache only with tags.
        /// </summary>
        Task SetInL1Async<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags);

        /// <summary>
        /// Sets a value in the L2 cache only.
        /// </summary>
        Task SetInL2Async<T>(string key, T value, TimeSpan expiration);

        /// <summary>
        /// Sets a value in the L3 cache only.
        /// </summary>
        Task SetInL3Async<T>(string key, T value, TimeSpan expiration);

        /// <summary>
        /// Sets a value in both L1 and L2 caches.
        /// </summary>
        Task SetInBothAsync<T>(string key, T value, TimeSpan l1Expiration, TimeSpan l2Expiration);

        /// <summary>
        /// Sets a value in all available cache layers.
        /// </summary>
        Task SetInAllAsync<T>(string key, T value, TimeSpan l1Expiration, TimeSpan l2Expiration, TimeSpan l3Expiration);

        /// <summary>
        /// Invalidates a key from the L1 cache only.
        /// </summary>
        Task InvalidateL1Async(string key);

        /// <summary>
        /// Invalidates a key from the L2 cache only.
        /// </summary>
        Task InvalidateL2Async(string key);

        /// <summary>
        /// Invalidates a key from the L3 cache only.
        /// </summary>
        Task InvalidateL3Async(string key);

        /// <summary>
        /// Invalidates a key from both L1 and L2 caches.
        /// </summary>
        Task InvalidateBothAsync(string key);

        /// <summary>
        /// Invalidates a key from all cache layers.
        /// </summary>
        Task InvalidateAllAsync(string key);

        /// <summary>
        /// Warms the L1 cache from the L2 cache for specified keys.
        /// </summary>
        Task WarmL1CacheAsync(params string[] keys);

        /// <summary>
        /// Gets statistics about the hybrid cache.
        /// </summary>
        Task<HybridCacheStats> GetStatsAsync();

        /// <summary>
        /// Evicts items from L1 cache based on the configured eviction policy.
        /// </summary>
        Task EvictFromL1Async(string key);

        /// <summary>
        /// Synchronizes L1 cache across all instances.
        /// </summary>
        Task SyncL1CacheAsync();
    }

    /// <summary>
    /// Statistics for the hybrid cache.
    /// </summary>
    public class HybridCacheStats
    {
        /// <summary>
        /// L1 cache hits.
        /// </summary>
        public long L1Hits { get; init; }

        /// <summary>
        /// L1 cache misses.
        /// </summary>
        public long L1Misses { get; init; }

        /// <summary>
        /// L2 cache hits.
        /// </summary>
        public long L2Hits { get; init; }

        /// <summary>
        /// L2 cache misses.
        /// </summary>
        public long L2Misses { get; init; }

        /// <summary>
        /// L3 cache hits.
        /// </summary>
        public long L3Hits { get; init; }

        /// <summary>
        /// L3 cache misses.
        /// </summary>
        public long L3Misses { get; init; }

        /// <summary>
        /// Number of L1 cache entries.
        /// </summary>
        public long L1Entries { get; init; }

        /// <summary>
        /// Number of L1 cache evictions.
        /// </summary>
        public long L1Evictions { get; init; }

        /// <summary>
        /// Number of backplane messages sent.
        /// </summary>
        public long BackplaneMessagesSent { get; init; }

        /// <summary>
        /// Number of backplane messages received.
        /// </summary>
        public long BackplaneMessagesReceived { get; init; }

        /// <summary>
        /// L1 hit ratio.
        /// </summary>
        public double L1HitRatio => L1Hits + L1Misses > 0 ? (double)L1Hits / (L1Hits + L1Misses) : 0;

        /// <summary>
        /// L2 hit ratio.
        /// </summary>
        public double L2HitRatio => L2Hits + L2Misses > 0 ? (double)L2Hits / (L2Hits + L2Misses) : 0;

        /// <summary>
        /// L3 hit ratio.
        /// </summary>
        public double L3HitRatio => L3Hits + L3Misses > 0 ? (double)L3Hits / (L3Hits + L3Misses) : 0;

        /// <summary>
        /// Overall hit ratio.
        /// </summary>
        public double OverallHitRatio
        {
            get
            {
                var totalHits = L1Hits + L2Hits + L3Hits;
                var totalRequests = L1Hits + L1Misses;
                return totalRequests > 0 ? (double)totalHits / totalRequests : 0;
            }
        }

        /// <summary>
        /// Number of tag-to-key mappings being tracked.
        /// </summary>
        public int TagMappingCount { get; init; }

        /// <summary>
        /// Number of unique tags being tracked.
        /// </summary>
        public int UniqueTagCount { get; init; }

        /// <summary>
        /// Whether efficient tag-based L1 invalidation is enabled.
        /// </summary>
        public bool EfficientTagInvalidationEnabled { get; init; }
    }
}