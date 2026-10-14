using System.Diagnostics;
using MethodCache.Infrastructure;

namespace MethodCache.Extensibility
{
    /// <summary>
    /// A cache manager that never caches: every call runs the factory and every lookup misses. Useful in tests, and
    /// for code that uses <c>GetOrCreateAsync</c> directly.
    /// </summary>
    public class NoOpCacheManager : ICacheManager
    {
        private readonly ICacheMetricsProvider? _metricsProvider;

        /// <summary>Creates a cache manager that never caches.</summary>
        public NoOpCacheManager()
        {
        }

        /// <summary>Creates a cache manager that never caches and reports every call as a miss.</summary>
        public NoOpCacheManager(ICacheMetricsProvider? metricsProvider)
        {
            _metricsProvider = metricsProvider;
        }

        // ============= New CacheRuntimePolicy-based methods (primary implementation) =============

        /// <inheritdoc />
        public Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
        {
            // Always execute the factory, effectively disabling caching
            return RunAsync(methodName, factory);
        }

        /// <inheritdoc />
        public ValueTask<CacheLookupResult<T>> LookupAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
            => new(CacheLookupResult<T>.Miss);

        /// <inheritdoc />
        public ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
        {
            // Always return cache miss for no-op cache
            return new ValueTask<T?>(default(T));
        }

        /// <inheritdoc />
        public ValueTask<T?> TryGetFastAsync<T>(string cacheKey)
        {
            // Always return cache miss for no-op cache
            return new ValueTask<T?>(default(T));
        }

        /// <inheritdoc />
        public bool TryGetFast<T>(string cacheKey, out T? value)
        {
            value = default;
            return false;
        }

        /// <inheritdoc />
        public Task<T> GetOrCreateFastAsync<T>(string cacheKey, string methodName, Func<Task<T>> factory, CacheRuntimePolicy policy, CancellationToken cancellationToken = default)
        {
            // Always execute the factory, effectively disabling caching
            return RunAsync(methodName, factory);
        }

        private async Task<T> RunAsync<T>(string methodName, Func<Task<T>> factory)
        {
            if (_metricsProvider == null)
            {
                return await factory().ConfigureAwait(false);
            }

            _metricsProvider.CacheMiss(methodName);
            var start = Stopwatch.GetTimestamp();
            try
            {
                var result = await factory().ConfigureAwait(false);
                _metricsProvider.CacheLatency(methodName, Stopwatch.GetElapsedTime(start));
                return result;
            }
            catch (Exception ex)
            {
                _metricsProvider.CacheError(methodName, ex.Message);
                throw;
            }
        }

        // ============= Invalidation methods =============

        /// <inheritdoc />
        public Task InvalidateByTagsAsync(params string[] tags)
        {
            // No operation for invalidation
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task InvalidateByKeysAsync(params string[] keys)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task InvalidateByTagPatternAsync(string pattern)
        {
            return Task.CompletedTask;
        }
    }
}
