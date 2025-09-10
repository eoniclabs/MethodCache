using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Hybrid
{
    public interface IL1Cache
    {
        Task<T?> GetAsync<T>(string key);
        Task SetAsync<T>(string key, T value, TimeSpan expiration);
        Task<bool> RemoveAsync(string key);
        Task ClearAsync();
        Task<bool> ExistsAsync(string key);
        Task<IEnumerable<string>> GetKeysAsync(string pattern = "*");
        Task<long> GetCountAsync();
        Task<L1CacheStats> GetStatsAsync();
        Task EvictExpiredAsync();
        Task<bool> TryEvictLRUAsync();
    }

    public class L1CacheStats
    {
        public long Hits { get; set; }
        public long Misses { get; set; }
        public long Evictions { get; set; }
        public long Entries { get; set; }
        public long MemoryUsageBytes { get; set; }
        public double HitRatio => Hits + Misses > 0 ? (double)Hits / (Hits + Misses) : 0;
    }

    public class L1CacheEntry<T>
    {
        public T Value { get; set; } = default!;
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime LastAccessedAt { get; set; }
        public long AccessCount { get; set; }
        public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
        public bool SlidingExpiration { get; set; }
        public TimeSpan SlidingWindow { get; set; }

        public void UpdateAccess()
        {
            LastAccessedAt = DateTime.UtcNow;
            AccessCount++;
            
            if (SlidingExpiration)
            {
                ExpiresAt = DateTime.UtcNow.Add(SlidingWindow);
            }
        }
    }
}