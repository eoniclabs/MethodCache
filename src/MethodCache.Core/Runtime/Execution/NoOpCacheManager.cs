using MethodCache.Infrastructure;

namespace MethodCache.Extensibility
{
    /// <summary>
    /// A cache manager that never caches: every call runs the factory and every lookup misses. Useful in tests, and
    /// for code that uses <c>GetOrCreateAsync</c> directly.
    /// </summary>
    public partial class NoOpCacheManager : ICacheRuntime
    {

        /// <summary>Creates a cache manager that never caches.</summary>
        public NoOpCacheManager()
        {
        }

        Task<T> ICacheRuntime.GetOrCreateAsync<T>(string name, object[] args, Func<Task<T>> factory, CacheRuntimePolicy policy, ICacheKeyGenerator generator) => GetOrCreateAsync(name, args, factory, policy, generator);
        ValueTask<T?> ICacheRuntime.TryGetAsync<T>(string name, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator generator) where T : default => TryGetAsync<T>(name, args, policy, generator);
        ValueTask<CacheLookupResult<T>> ICacheRuntime.LookupAsync<T>(string name, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator generator) => LookupAsync<T>(name, args, policy, generator);
        ValueTask<T?> ICacheRuntime.TryGetFastAsync<T>(string key) where T : default => TryGetFastAsync<T>(key);
        Task<T> ICacheRuntime.GetOrCreateFastAsync<T>(string key, string name, Func<Task<T>> factory, CacheRuntimePolicy policy, CancellationToken token) => GetOrCreateFastAsync(key, name, factory, policy, token);
        Task ICacheRuntime.InvalidateByKeysAsync(params string[] keys) => InvalidateByKeysAsync(keys);
        Task ICacheRuntime.InvalidateByTagsAsync(params string[] tags) => InvalidateByTagsAsync(tags);
        Task ICacheRuntime.InvalidateByTagPatternAsync(string pattern) => InvalidateByTagPatternAsync(pattern);

        // ============= New CacheRuntimePolicy-based methods (primary implementation) =============

        /// <inheritdoc />
        internal Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
        {
            // Always execute the factory, effectively disabling caching
            return RunAsync(methodName, factory);
        }

        /// <inheritdoc />
        internal ValueTask<CacheLookupResult<T>> LookupAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
            => new(CacheLookupResult<T>.Miss);

        /// <inheritdoc />
        internal ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
        {
            // Always return cache miss for no-op cache
            return new ValueTask<T?>(default(T));
        }

        /// <inheritdoc />
        internal ValueTask<T?> TryGetFastAsync<T>(string cacheKey)
        {
            // Always return cache miss for no-op cache
            return new ValueTask<T?>(default(T));
        }

        /// <inheritdoc />
        internal bool TryGetFast<T>(string cacheKey, out T? value)
        {
            value = default;
            return false;
        }

        /// <inheritdoc />
        internal Task<T> GetOrCreateFastAsync<T>(string cacheKey, string methodName, Func<Task<T>> factory, CacheRuntimePolicy policy, CancellationToken cancellationToken = default)
        {
            // Always execute the factory, effectively disabling caching
            return RunAsync(methodName, factory);
        }

        private static Task<T> RunAsync<T>(string methodName, Func<Task<T>> factory) => factory();

        // ============= Invalidation methods =============

        /// <inheritdoc />
        internal Task InvalidateByTagsAsync(params string[] tags)
        {
            // No operation for invalidation
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        internal Task InvalidateByKeysAsync(params string[] keys)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        internal Task InvalidateByTagPatternAsync(string pattern)
        {
            return Task.CompletedTask;
        }
    }
}
