using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MethodCache.Core.Configuration;

namespace MethodCache.Core
{
    public class InMemoryCacheManager : ICacheManager
    {
        private class CacheEntry
        {
            public object Value { get; set; } = null!;
            public HashSet<string> Tags { get; set; } = new HashSet<string>();
            public DateTimeOffset AbsoluteExpiration { get; set; }
        }

        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new ConcurrentDictionary<string, CacheEntry>();
        private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _stampedePrevention = new ConcurrentDictionary<string, Lazy<Task<object>>>();
        private readonly ICacheMetricsProvider _metricsProvider;

        public InMemoryCacheManager(ICacheMetricsProvider metricsProvider)
        {
            _metricsProvider = metricsProvider;
        }

        public async Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator, bool requireIdempotent)
        {
            var key = keyGenerator.GenerateKey(methodName, args, settings);

            // Check cache first - fast memory operation
            if (_cache.TryGetValue(key, out var entry) && entry.AbsoluteExpiration > DateTimeOffset.UtcNow)
            {
                _metricsProvider.CacheHit(methodName);
                return (T)entry.Value;
            }

            // Validate idempotency requirement
            if (requireIdempotent && !settings.IsIdempotent)
            {
                throw new InvalidOperationException($"Method {methodName} is not marked as idempotent, but caching requires it.");
            }

            // Handle cache miss with stampede prevention
            var lazyTask = _stampedePrevention.GetOrAdd(key, _ => new Lazy<Task<object>>(async () =>
            {
                try
                {
                    // Let the factory method run without timeout - service layer handles resilience
                    var result = await factory().ConfigureAwait(false);
                    
                    if (result != null)
                    {
                        var newEntry = new CacheEntry
                        {
                            Value = result,
                            Tags = new HashSet<string>(settings.Tags),
                            AbsoluteExpiration = DateTimeOffset.UtcNow.Add(settings.Duration ?? TimeSpan.FromMinutes(5))
                        };
                        _cache.TryAdd(key, newEntry);
                    }
                    return result!;
                }
                catch (Exception ex)
                {
                    // Log the error but let it propagate - service layer should handle retries/fallbacks
                    _metricsProvider.CacheError(methodName, ex.Message);
                    throw;
                }
            }));

            var finalResult = await lazyTask.Value.ConfigureAwait(false);
            _stampedePrevention.TryRemove(key, out _); // Clean up after task completes

            _metricsProvider.CacheMiss(methodName);
            return (T)finalResult;
        }

        public Task InvalidateByTagsAsync(params string[] tags)
        {
            foreach (var tag in tags)
            {
                var keysToRemove = _cache.Where(kvp => kvp.Value.Tags.Contains(tag)).Select(kvp => kvp.Key).ToList();
                foreach (var key in keysToRemove)
                {
                    _cache.TryRemove(key, out _);
                }
            }
            return Task.CompletedTask;
        }
    }
}