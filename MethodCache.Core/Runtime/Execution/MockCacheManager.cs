using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace MethodCache.Core.Runtime.Defaults
{
    public class MockCacheManager : ICacheManager
    {
        private readonly ConcurrentDictionary<string, object> _cache = new ConcurrentDictionary<string, object>();
        public bool ForceCacheHit { get; set; }
        public bool ForceCacheMiss { get; set; }

        // ============= New CacheRuntimePolicy-based methods (primary implementation) =============

        public Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
        {
            var key = keyGenerator.GenerateKey(methodName, args, policy);

            if (ForceCacheHit && _cache.TryGetValue(key, out var hitValue))
            {
                return Task.FromResult((T)hitValue);
            }

            if (ForceCacheMiss || !_cache.TryGetValue(key, out var value))
            {
                var result = factory().Result; // Blocking call for simplicity in mock
                if (result != null)
                {
                    _cache.TryAdd(key, result);
                }
                return Task.FromResult(result);
            }

            return Task.FromResult((T)value);
        }

        public ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
        {
            var key = keyGenerator.GenerateKey(methodName, args, policy);

            if (ForceCacheMiss || !_cache.TryGetValue(key, out var value))
            {
                return new ValueTask<T?>(default(T));
            }

            return new ValueTask<T?>((T)value);
        }

        // ============= Invalidation methods =============

        public Task InvalidateByTagsAsync(params string[] tags)
        {
            // For simplicity, this mock doesn't fully support tag-based invalidation yet.
            return Task.CompletedTask;
        }

        public Task InvalidateByKeysAsync(params string[] keys)
        {
            if (keys == null)
            {
                return Task.CompletedTask;
            }

            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                _cache.TryRemove(key, out _);
            }

            return Task.CompletedTask;
        }

        public Task InvalidateByTagPatternAsync(string pattern)
        {
            // Pattern invalidation not required for lightweight mock
            return Task.CompletedTask;
        }

        public void Clear()
        {
            _cache.Clear();
        }
    }
}
