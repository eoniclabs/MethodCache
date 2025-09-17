using MethodCache.Core;
using MethodCache.Core.Configuration;
using System.Collections.Concurrent;

namespace MethodCache.SampleApp.Infrastructure
{
    /// <summary>
    /// Custom cache manager that demonstrates advanced caching features
    /// with statistics tracking and custom eviction policies
    /// </summary>
    public class CustomCacheManager : ICacheManager
    {
        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
        private readonly ICacheMetricsProvider _metricsProvider;
        private readonly Timer _cleanupTimer;

        public CustomCacheManager(ICacheMetricsProvider metricsProvider)
        {
            _metricsProvider = metricsProvider;
            // Cleanup expired entries every 5 minutes
            _cleanupTimer = new Timer(CleanupExpiredEntries, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        public async Task<T> GetOrCreateAsync<T>(
            string methodName, 
            object[] args, 
            Func<Task<T>> factory, 
            CacheMethodSettings settings, 
            ICacheKeyGenerator keyGenerator, 
            bool requireIdempotent)
        {
            var startTime = DateTime.UtcNow;
            var cacheKey = keyGenerator.GenerateKey(methodName, args, settings);

            try
            {
                // Check if value exists and is not expired
                if (_cache.TryGetValue(cacheKey, out var entry) && !entry.IsExpired)
                {
                    _metricsProvider.CacheHit(methodName);
                    _metricsProvider.CacheLatency(methodName, (long)(DateTime.UtcNow - startTime).TotalMilliseconds);
                    
                    Console.WriteLine($"[CACHE HIT] {methodName} (Key: {cacheKey[..Math.Min(20, cacheKey.Length)]}...)");
                    return (T)entry.Value;
                }

                // Cache miss - execute the factory method
                _metricsProvider.CacheMiss(methodName);
                Console.WriteLine($"[CACHE MISS] {methodName} (Key: {cacheKey[..Math.Min(20, cacheKey.Length)]}...)");

                var result = await factory();
                
                // Store in cache with expiration
                var duration = settings.Duration ?? TimeSpan.FromMinutes(5);
                var newEntry = new CacheEntry
                {
                    Value = result!,
                    ExpiresAt = DateTime.UtcNow.Add(duration),
                    CreatedAt = DateTime.UtcNow,
                    Tags = settings.Tags?.ToHashSet() ?? new HashSet<string>(),
                    AccessCount = 0,
                    LastAccessedAt = DateTime.UtcNow
                };

                _cache.AddOrUpdate(cacheKey, newEntry, (key, oldEntry) => newEntry);
                
                _metricsProvider.CacheLatency(methodName, (long)(DateTime.UtcNow - startTime).TotalMilliseconds);
                Console.WriteLine($"[CACHE STORE] {methodName} (TTL: {duration.TotalMinutes:F1}m)");

                return result;
            }
            catch (Exception ex)
            {
                _metricsProvider.CacheError(methodName, ex.Message);
                Console.WriteLine($"[CACHE ERROR] {methodName}: {ex.Message}");
                throw;
            }
        }

        public ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator)
        {
            var cacheKey = keyGenerator.GenerateKey(methodName, args, settings);
            
            if (_cache.TryGetValue(cacheKey, out var entry) && !entry.IsExpired)
            {
                _metricsProvider.CacheHit(methodName);
                Console.WriteLine($"[CACHE HIT] {methodName} (Key: {cacheKey[..Math.Min(20, cacheKey.Length)]}...)");
                return new ValueTask<T?>((T)entry.Value);
            }
            
            _metricsProvider.CacheMiss(methodName);
            Console.WriteLine($"[CACHE MISS] {methodName} (Key: {cacheKey[..Math.Min(20, cacheKey.Length)]}...)");
            return new ValueTask<T?>(default(T));
        }

        public async Task InvalidateByTagsAsync(params string[] tags)
        {
            var invalidatedCount = 0;
            var keysToRemove = new List<string>();

            foreach (var kvp in _cache)
            {
                if (kvp.Value.Tags.Any(tag => tags.Contains(tag)))
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                if (_cache.TryRemove(key, out _))
                {
                    invalidatedCount++;
                }
            }

            Console.WriteLine($"[CACHE INVALIDATE] Removed {invalidatedCount} entries for tags: {string.Join(", ", tags)}");
            await Task.CompletedTask;
        }

        public void ClearAll()
        {
            var count = _cache.Count;
            _cache.Clear();
            Console.WriteLine($"[CACHE CLEAR] Removed all {count} cache entries");
        }

        public CacheStatistics GetStatistics()
        {
            var totalEntries = _cache.Count;
            var expiredEntries = _cache.Values.Count(e => e.IsExpired);
            var totalMemoryBytes = _cache.Values.Sum(e => EstimateMemoryUsage(e.Value));

            return new CacheStatistics
            {
                TotalEntries = totalEntries,
                ExpiredEntries = expiredEntries,
                ActiveEntries = totalEntries - expiredEntries,
                EstimatedMemoryUsageBytes = totalMemoryBytes,
                GeneratedAt = DateTime.UtcNow
            };
        }

        private void CleanupExpiredEntries(object? state)
        {
            var expiredKeys = _cache
                .Where(kvp => kvp.Value.IsExpired)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _cache.TryRemove(key, out _);
            }

            if (expiredKeys.Count > 0)
            {
                Console.WriteLine($"[CACHE CLEANUP] Removed {expiredKeys.Count} expired entries");
            }
        }

        private static long EstimateMemoryUsage(object obj)
        {
            // Simple estimation - in real scenarios you might use more sophisticated methods
            return obj switch
            {
                string str => str.Length * 2, // Unicode characters
                int => 4,
                long => 8,
                decimal => 16,
                DateTime => 8,
                _ => 100 // Default estimate for complex objects
            };
        }

        public void Dispose()
        {
            _cleanupTimer?.Dispose();
        }

        private class CacheEntry
        {
            public object Value { get; set; } = default!;
            public DateTime ExpiresAt { get; set; }
            public DateTime CreatedAt { get; set; }
            public HashSet<string> Tags { get; set; } = new();
            public int AccessCount { get; set; }
            public DateTime LastAccessedAt { get; set; }

            public bool IsExpired => DateTime.UtcNow > ExpiresAt;
        }
    }

    public class CacheStatistics
    {
        public int TotalEntries { get; set; }
        public int ActiveEntries { get; set; }
        public int ExpiredEntries { get; set; }
        public long EstimatedMemoryUsageBytes { get; set; }
        public DateTime GeneratedAt { get; set; }
    }
}