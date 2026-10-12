using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;

namespace MethodCache.SourceGenerator.IntegrationTests.Infrastructure
{
    /// <summary>
    /// Cache entry with expiration support
    /// </summary>
    public class CacheEntry
    {
        public object Value { get; set; } = null!;
        public DateTimeOffset ExpiryTime { get; set; }
    }

    /// <summary>
    /// Mock cache manager that integrates with metrics provider for testing
    /// </summary>
    public class TestMockCacheManager : ICacheManager
    {
        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
        private readonly ICacheMetricsProvider _metricsProvider;
        private readonly TimeProvider _time;

        /// <summary>
        /// Reads time from <paramref name="timeProvider"/>, so a test that registers a FakeTimeProvider controls expiry.
        /// </summary>
        public TestMockCacheManager(ICacheMetricsProvider metricsProvider, TimeProvider? timeProvider = null)
        {
            _metricsProvider = metricsProvider;
            _time = timeProvider ?? TimeProvider.System;
        }

        // Like the real cache, an entry expires exactly at the end of its Duration
        private bool IsExpired(CacheEntry entry) => _time.GetUtcNow() >= entry.ExpiryTime;

        // ============= New CacheRuntimePolicy-based methods (primary implementation) =============

        public async Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
        {
            var key = keyGenerator.GenerateKey(methodName, args, policy);

            // Check cache first and verify not expired
            if (_cache.TryGetValue(key, out var cacheEntry))
            {
                if (!IsExpired(cacheEntry))
                {
                    _metricsProvider.CacheHit(methodName);
                    return (T)cacheEntry.Value;
                }
                else
                {
                    // Remove expired entry
                    _cache.TryRemove(key, out _);
                }
            }

            // Cache miss - call factory
            _metricsProvider.CacheMiss(methodName);
            try
            {
                var result = await factory();
                if (result != null)
                {
                    var expiryTime = _time.GetUtcNow().Add(policy.Duration ?? TimeSpan.FromMinutes(5));
                    var newEntry = new CacheEntry
                    {
                        Value = result,
                        ExpiryTime = expiryTime
                    };
                    _cache.TryAdd(key, newEntry);
                }
                return result;
            }
            catch (Exception ex)
            {
                _metricsProvider.CacheError(methodName, ex.Message);
                throw;
            }
        }

        /// <inheritdoc />
        public ValueTask<CacheLookupResult<T>> LookupAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
            => new(CacheLookupResult<T>.Miss);

        public ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheRuntimePolicy policy, ICacheKeyGenerator keyGenerator)
        {
            var key = keyGenerator.GenerateKey(methodName, args, policy);

            if (_cache.TryGetValue(key, out var cacheEntry))
            {
                if (!IsExpired(cacheEntry))
                {
                    return ValueTask.FromResult((T?)cacheEntry.Value);
                }
                else
                {
                    // Remove expired entry
                    _cache.TryRemove(key, out _);
                }
            }

            return ValueTask.FromResult(default(T));
        }

        public ValueTask<T?> TryGetFastAsync<T>(string cacheKey)
        {
            if (_cache.TryGetValue(cacheKey, out var cacheEntry) && !IsExpired(cacheEntry))
            {
                return ValueTask.FromResult((T?)cacheEntry.Value);
            }

            return ValueTask.FromResult(default(T));
        }

        public bool TryGetFast<T>(string cacheKey, out T? value)
        {
            if (_cache.TryGetValue(cacheKey, out var cacheEntry) && !IsExpired(cacheEntry))
            {
                value = (T)cacheEntry.Value;
                return true;
            }

            value = default;
            return false;
        }

        public async Task<T> GetOrCreateFastAsync<T>(string cacheKey, string methodName, Func<Task<T>> factory, CacheRuntimePolicy policy, CancellationToken cancellationToken = default)
        {
            // Check cache first and verify not expired
            if (_cache.TryGetValue(cacheKey, out var cacheEntry))
            {
                if (!IsExpired(cacheEntry))
                {
                    // The decorator only reports hits it serves itself; this one is ours to report.
                    _metricsProvider.CacheHit(methodName);
                    return (T)cacheEntry.Value;
                }
                else
                {
                    // Remove expired entry
                    _cache.TryRemove(cacheKey, out _);
                }
            }

            // Cache miss - call factory
            _metricsProvider.CacheMiss(methodName);
            try
            {
                var result = await factory();
                if (result != null)
                {
                    var expiryTime = _time.GetUtcNow().Add(policy.Duration ?? TimeSpan.FromMinutes(5));
                    var newEntry = new CacheEntry
                    {
                        Value = result,
                        ExpiryTime = expiryTime
                    };
                    _cache.TryAdd(cacheKey, newEntry);
                }
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
            // Record invalidation if the metrics provider supports it
            if (_metricsProvider is TestCacheMetricsProvider testProvider)
            {
                testProvider.RecordInvalidation(tags);
            }

            // For simplicity, clear entire cache on any invalidation
            _cache.Clear();
            return Task.CompletedTask;
        }

        public Task InvalidateByKeysAsync(params string[] keys)
        {
            // For simplicity, clear entire cache on any invalidation
            _cache.Clear();
            return Task.CompletedTask;
        }

        public Task InvalidateByTagPatternAsync(string pattern)
        {
            // For simplicity, clear entire cache on any invalidation
            _cache.Clear();
            return Task.CompletedTask;
        }
    }
}
