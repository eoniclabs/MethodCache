using System;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Configuration;

namespace MethodCache.Providers.Redis.Hybrid
{
    public interface IHybridCacheManager : ICacheManager
    {
        Task<T?> GetFromL1Async<T>(string key);
        Task<T?> GetFromL2Async<T>(string key);
        Task SetInL1Async<T>(string key, T value, TimeSpan expiration);
        Task SetInL2Async<T>(string key, T value, TimeSpan expiration);
        Task InvalidateL1Async(string key);
        Task InvalidateL2Async(string key);
        Task InvalidateBothAsync(string key);
        Task<HybridCacheStats> GetStatsAsync();
        Task WarmL1FromL2Async(string key);
        Task EvictFromL1Async(string key);
    }

    public class HybridCacheStats
    {
        public long L1Hits { get; set; }
        public long L1Misses { get; set; }
        public long L2Hits { get; set; }
        public long L2Misses { get; set; }
        public long L1Evictions { get; set; }
        public long L1Entries { get; set; }
        public long L2Entries { get; set; }
        public double L1HitRatio => L1Hits + L1Misses > 0 ? (double)L1Hits / (L1Hits + L1Misses) : 0;
        public double L2HitRatio => L2Hits + L2Misses > 0 ? (double)L2Hits / (L2Hits + L2Misses) : 0;
        public double OverallHitRatio => (L1Hits + L2Hits) > 0 ? (double)(L1Hits + L2Hits) / (L1Hits + L1Misses + L2Hits + L2Misses) : 0;
    }
}