using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using MethodCache.Core.Configuration;

namespace MethodCache.Core.Runtime.Defaults
{
    public class MockCacheManager : ICacheManager
    {
        private readonly ConcurrentDictionary<string, object> _cache = new ConcurrentDictionary<string, object>();
        public bool ForceCacheHit { get; set; }
        public bool ForceCacheMiss { get; set; }

        public Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator, bool requireIdempotent)
        {
            var key = keyGenerator.GenerateKey(methodName, args, settings);
            System.Console.WriteLine($"MockCacheManager: key='{key}', keyGen='{keyGenerator.GetType().Name}', method='{methodName}', args=[{string.Join(",", args.Select(a => a?.GetType().Name ?? "null"))}], cache contains: {_cache.ContainsKey(key)}");

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

        public ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator)
        {
            var key = keyGenerator.GenerateKey(methodName, args, settings);
            
            if (ForceCacheMiss || !_cache.TryGetValue(key, out var value))
            {
                return new ValueTask<T?>(default(T));
            }
            
            return new ValueTask<T?>((T)value);
        }

        public void Clear()
        {
            _cache.Clear();
        }
    }
}
