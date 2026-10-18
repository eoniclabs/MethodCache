using System.Collections.Concurrent;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;

namespace MethodCache.Core.Tests.Core.Extensions
{
    internal sealed class MockCacheManager : ICacheManager
    {
        public ValueTask<T> GetOrCreateAsync<T>(string key, Func<CacheContext, CancellationToken, ValueTask<T>> factory, CacheEntryOptions? options, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (options?.Enabled == false) return factory(new CacheContext(key), token);
            var policy = options?.Runtime ?? CacheRuntimePolicy.Empty(CacheKeyLookup.MethodName);
            return new(GetOrCreateFastAsync(options?.GetCacheKey(key) ?? key, policy.MethodId, () => factory(new CacheContext(key), token).AsTask(), policy, token));
        }
        public ValueTask<CacheLookupResult<T>> TryGetAsync<T>(string key, int? version = null, CancellationToken token = default) => new(!ForceCacheMiss && _cache.TryGetValue(CacheManagerExtensions.GetCacheKey(key, version), out var value) ? CacheLookupResult<T>.Hit((T)value) : CacheLookupResult<T>.Miss);
        public ValueTask<InvalidationResult> InvalidateKeysAsync(IEnumerable<string> keys, int? version = null, CancellationToken token = default)
        { var values = keys.Select(key => CacheManagerExtensions.GetCacheKey(key, version)).Distinct().ToArray(); InvalidateByKeysAsync(values); return new(new InvalidationResult { Requested = values.Length, Local = values.Length }); }
        public ValueTask<InvalidationResult> InvalidateTagsAsync(IEnumerable<string> tags, CancellationToken token)
        { var values = tags.Distinct().ToArray(); return new(new InvalidationResult { Requested = values.Length, Local = values.Length }); }

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

        /// <inheritdoc />
        public ValueTask<CacheLookupResult<T>> LookupAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
        {
            var key = keyGenerator.GenerateKey(methodName, args, policy);
            return new(!ForceCacheMiss && _cache.TryGetValue(key, out var value)
                ? CacheLookupResult<T>.Hit((T)value) : CacheLookupResult<T>.Miss);
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

        public ValueTask<T?> TryGetFastAsync<T>(string cacheKey)
        {
            if (ForceCacheMiss || !_cache.TryGetValue(cacheKey, out var value))
            {
                return new ValueTask<T?>(default(T));
            }

            return new ValueTask<T?>((T)value);
        }

        public bool TryGetFast<T>(string cacheKey, out T? value)
        {
            if (ForceCacheMiss || !_cache.TryGetValue(cacheKey, out var stored))
            {
                value = default;
                return false;
            }

            value = (T)stored;
            return true;
        }

        public async Task<T> GetOrCreateFastAsync<T>(string cacheKey, string methodName, Func<Task<T>> factory, CacheRuntimePolicy policy, CancellationToken cancellationToken = default)
        {
            if (ForceCacheMiss || !_cache.TryGetValue(cacheKey, out var value))
            {
                var result = await factory().ConfigureAwait(false);
                if (result != null)
                {
                    _cache.TryAdd(cacheKey, result);
                }
                return result;
            }

            return (T)value;
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
