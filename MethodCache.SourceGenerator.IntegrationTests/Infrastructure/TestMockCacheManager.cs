using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Runtime;

namespace MethodCache.SourceGenerator.IntegrationTests.Infrastructure
{
    /// <summary>
    /// Cache entry with expiration support
    /// </summary>
    public class CacheEntry
    {
        public object Value { get; set; } = null!;
        public DateTime ExpiryTime { get; set; }
        
        public bool IsExpired => DateTime.UtcNow > ExpiryTime;
    }

    /// <summary>
    /// Mock cache manager that integrates with metrics provider for testing
    /// </summary>
    public class TestMockCacheManager : ICacheManager
    {
        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
        private readonly ICacheMetricsProvider _metricsProvider;

        public TestMockCacheManager(ICacheMetricsProvider metricsProvider)
        {
            _metricsProvider = metricsProvider;
        }

        // ============= New CacheRuntimePolicy-based methods (primary implementation) =============

        public async Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
        {
            var key = keyGenerator.GenerateKey(methodName, args, policy);

            // Check cache first and verify not expired
            if (_cache.TryGetValue(key, out var cacheEntry))
            {
                if (!cacheEntry.IsExpired)
                {
                    _metricsProvider.CacheHit(methodName);
                    return (T)cacheEntry.Value;
                }
                else
                {
                    // Remove expired entry
                    _cache.TryRemove(key, out _);
                }
            }

            // Cache miss - call factory
            _metricsProvider.CacheMiss(methodName);
            try
            {
                var result = await factory();
                if (result != null)
                {
                    var expiryTime = DateTime.UtcNow.Add(policy.Duration ?? TimeSpan.FromMinutes(5));
                    var newEntry = new CacheEntry
                    {
                        Value = result,
                        ExpiryTime = expiryTime
                    };
                    _cache.TryAdd(key, newEntry);
                }
                return result;
            }
            catch (Exception ex)
            {
                _metricsProvider.CacheError(methodName, ex.Message);
                throw;
            }
        }

        public ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
        {
            var key = keyGenerator.GenerateKey(methodName, args, policy);

            if (_cache.TryGetValue(key, out var cacheEntry))
            {
                if (!cacheEntry.IsExpired)
                {
                    return ValueTask.FromResult((T?)cacheEntry.Value);
                }
                else
                {
                    // Remove expired entry
                    _cache.TryRemove(key, out _);
                }
            }

            return ValueTask.FromResult(default(T));
        }

        // ============= Invalidation methods =============

        public Task InvalidateByTagsAsync(params string[] tags)
        {
            // Record invalidation if the metrics provider supports it
            if (_metricsProvider is TestCacheMetricsProvider testProvider)
            {
                testProvider.RecordInvalidation(tags);
            }

            // For simplicity, clear entire cache on any invalidation
            _cache.Clear();
            return Task.CompletedTask;
        }

        public Task InvalidateByKeysAsync(params string[] keys)
        {
            // For simplicity, clear entire cache on any invalidation
            _cache.Clear();
            return Task.CompletedTask;
        }

        public Task InvalidateByTagPatternAsync(string pattern)
        {
            // For simplicity, clear entire cache on any invalidation
            _cache.Clear();
            return Task.CompletedTask;
        }
    }
}