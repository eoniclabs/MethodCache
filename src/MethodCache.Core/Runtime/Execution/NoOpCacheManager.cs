using System.Diagnostics;
using MethodCache.Infrastructure;

namespace MethodCache.Extensibility
{
    public class NoOpCacheManager : ICacheManager
    {
        private readonly ICacheMetricsProvider? _metricsProvider;

        public NoOpCacheManager()
        {
        }

        /// <summary>Creates a cache manager that never caches and reports every call as a miss.</summary>
        public NoOpCacheManager(ICacheMetricsProvider? metricsProvider)
        {
            _metricsProvider = metricsProvider;
        }

        // ============= New CacheRuntimePolicy-based methods (primary implementation) =============

        public Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
        {
            // Always execute the factory, effectively disabling caching
            return RunAsync(methodName, factory);
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
                _metricsProvider.CacheLatency(methodName, (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                _metricsProvider.CacheError(methodName, ex.Message);
                throw;
            }
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
