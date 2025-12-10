using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Runtime.KeyGeneration;

namespace MethodCache.Core.Runtime.Execution
{
    public class NoOpCacheManager : ICacheManager
    {
        // ============= New CacheRuntimePolicy-based methods (primary implementation) =============

        public Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
        {
            // Always execute the factory, effectively disabling caching
            return factory();
        }

        public ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
        {
            // Always return cache miss for no-op cache
            return new ValueTask<T?>(default(T));
        }

        public ValueTask<T?> TryGetFastAsync<T>(string cacheKey)
        {
            // Always return cache miss for no-op cache
            return new ValueTask<T?>(default(T));
        }

        public bool TryGetFast<T>(string cacheKey, out T? value)
        {
            value = default;
            return false;
        }

        public Task<T> GetOrCreateFastAsync<T>(string cacheKey, string methodName, Func<Task<T>> factory, CacheRuntimePolicy policy)
        {
            // Always execute the factory, effectively disabling caching
            return factory();
        }

        // ============= Invalidation methods =============

        public Task InvalidateByTagsAsync(params string[] tags)
        {
            // No operation for invalidation
            return Task.CompletedTask;
        }

        public Task InvalidateByKeysAsync(params string[] keys)
        {
            return Task.CompletedTask;
        }

        public Task InvalidateByTagPatternAsync(string pattern)
        {
            return Task.CompletedTask;
        }
    }
}
