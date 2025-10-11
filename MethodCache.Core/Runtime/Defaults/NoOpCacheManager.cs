using System;
using System.Threading.Tasks;
using MethodCache.Core.Configuration;

namespace MethodCache.Core.Runtime.Defaults
{
    public class NoOpCacheManager : ICacheManager
    {
        public Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator, bool requireIdempotent)
        {
            // Always execute the factory, effectively disabling caching
            return factory();
        }

        public Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheRuntimeDescriptor descriptor, ICacheKeyGenerator keyGenerator)
            => GetOrCreateAsync(methodName, args, factory, descriptor.ToCacheMethodSettings(), keyGenerator, descriptor.RequireIdempotent);

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

        public ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator)
        {
            // Always return cache miss for no-op cache
            return new ValueTask<T?>(default(T));
        }

        public ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheRuntimeDescriptor descriptor, ICacheKeyGenerator keyGenerator)
            => TryGetAsync<T>(methodName, args, descriptor.ToCacheMethodSettings(), keyGenerator);
    }
}
