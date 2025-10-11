using System;
using System.Threading.Tasks;

namespace MethodCache.Core.Runtime.Defaults
{
    public class NoOpCacheManager : ICacheManager
    {
        public Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheRuntimeDescriptor descriptor, ICacheKeyGenerator keyGenerator)
        {
            // Always execute the factory, effectively disabling caching
            return factory();
        }

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

        public ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheRuntimeDescriptor descriptor, ICacheKeyGenerator keyGenerator)
        {
            // Always return cache miss for no-op cache
            return new ValueTask<T?>(default(T));
        }
    }
}
