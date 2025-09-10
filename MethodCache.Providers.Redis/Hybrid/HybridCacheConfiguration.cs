using System;

namespace MethodCache.Providers.Redis.Hybrid
{
    public class HybridCacheOptions
    {
        // L1 (Memory) Cache Configuration
        public long L1MaxItems { get; set; } = 10000;
        public TimeSpan L1DefaultExpiration { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan L1MaxExpiration { get; set; } = TimeSpan.FromHours(1);
        public L1EvictionPolicy L1EvictionPolicy { get; set; } = L1EvictionPolicy.LRU;
        public bool L1SlidingExpiration { get; set; } = true;
        
        // L2 (Redis) Cache Configuration  
        public TimeSpan L2DefaultExpiration { get; set; } = TimeSpan.FromHours(4);
        public bool L2Enabled { get; set; } = true;
        
        // Hybrid Strategy Configuration
        public HybridStrategy Strategy { get; set; } = HybridStrategy.WriteThrough;
        public bool EnableL1Warming { get; set; } = true;
        public bool EnableL1Invalidation { get; set; } = true;
        public bool EnableStaleWhileRevalidate { get; set; } = false;
        public TimeSpan StaleWhileRevalidateWindow { get; set; } = TimeSpan.FromMinutes(1);
        
        // Performance Optimization
        public bool EnableAsyncL2Writes { get; set; } = true;
        public int MaxConcurrentL2Operations { get; set; } = 10;
        public TimeSpan L2OperationTimeout { get; set; } = TimeSpan.FromSeconds(5);
        
        // Statistics and Monitoring
        public bool EnableStatistics { get; set; } = true;
        public TimeSpan StatisticsWindow { get; set; } = TimeSpan.FromMinutes(5);
        
        // Cache Coherence
        public bool EnableCrossInstanceInvalidation { get; set; } = true;
        public TimeSpan CoherenceCheckInterval { get; set; } = TimeSpan.FromSeconds(30);
    }

    public enum HybridStrategy
    {
        /// <summary>
        /// Write to L1 and L2 synchronously
        /// </summary>
        WriteThrough,
        
        /// <summary>
        /// Write to L1 immediately, L2 asynchronously
        /// </summary>
        WriteBack,
        
        /// <summary>
        /// Write only to L1, L2 populated on miss
        /// </summary>
        L1Only,
        
        /// <summary>
        /// Bypass L1, use only L2 (Redis)
        /// </summary>
        L2Only
    }

    public enum L1EvictionPolicy
    {
        /// <summary>
        /// Least Recently Used
        /// </summary>
        LRU,
        
        /// <summary>
        /// Least Frequently Used
        /// </summary>
        LFU,
        
        /// <summary>
        /// First In, First Out
        /// </summary>
        FIFO,
        
        /// <summary>
        /// Time To Live based
        /// </summary>
        TTL
    }
}