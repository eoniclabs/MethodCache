using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MethodCache.Core.Configuration;
using Polly;
using Polly.CircuitBreaker;

namespace MethodCache.Core
{
    public class InMemoryCacheManager : ICacheManager
    {
        private class CacheEntry
        {
            public object Value { get; set; } = null!;
            public HashSet<string> Tags { get; set; } = new HashSet<string>();
            public DateTimeOffset AbsoluteExpiration { get; set; }
        }

        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new ConcurrentDictionary<string, CacheEntry>();
        private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _stampedePrevention = new ConcurrentDictionary<string, Lazy<Task<object>>>();
        private readonly ResiliencePipeline _circuitBreaker;
        private readonly ICacheMetricsProvider _metricsProvider;

        public InMemoryCacheManager(ICacheMetricsProvider metricsProvider)
        {
            _metricsProvider = metricsProvider;
            _circuitBreaker = new ResiliencePipelineBuilder()
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions()
                {
                    FailureRatio = 0.5,
                    MinimumThroughput = 2,
                    BreakDuration = TimeSpan.FromSeconds(5)
                })
                .AddTimeout(TimeSpan.FromSeconds(1))
                .Build();
        }

        public async Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator, bool requireIdempotent)
        {
            var key = keyGenerator.GenerateKey(methodName, args, settings);

            try
            {
                return await _circuitBreaker.ExecuteAsync(async token =>
                {
                    if (_cache.TryGetValue(key, out var entry) && entry.AbsoluteExpiration > DateTimeOffset.UtcNow)
                    {
                        _metricsProvider.CacheHit(methodName);
                        return (T)entry.Value;
                    }

                    if (requireIdempotent && !settings.IsIdempotent)
                    {
                        throw new InvalidOperationException($"Method {methodName} is not marked as idempotent, but caching requires it.");
                    }

                    var lazyTask = _stampedePrevention.GetOrAdd(key, _ => new Lazy<Task<object>>(async () =>
                    {
                        var result = await factory().ConfigureAwait(false);
                        if (result != null)
                        {
                            var newEntry = new CacheEntry
                            {
                                Value = result,
                                Tags = new HashSet<string>(settings.Tags),
                                AbsoluteExpiration = DateTimeOffset.UtcNow.Add(settings.Duration ?? TimeSpan.FromMinutes(5)) // Default duration
                            };
                            _cache.TryAdd(key, newEntry);
                        }
                        return result!;
                    }));

                    var finalResult = await lazyTask.Value.ConfigureAwait(false);
                    _stampedePrevention.TryRemove(key, out _); // Clean up after task completes

                    _metricsProvider.CacheMiss(methodName);
                    return (T)finalResult;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _metricsProvider.CacheError(methodName, ex.Message);
                return await factory().ConfigureAwait(false);
            }
        }

        public Task InvalidateByTagsAsync(params string[] tags)
        {
            foreach (var tag in tags)
            {
                var keysToRemove = _cache.Where(kvp => kvp.Value.Tags.Contains(tag)).Select(kvp => kvp.Key).ToList();
                foreach (var key in keysToRemove)
                {
                    _cache.TryRemove(key, out _);
                }
            }
            return Task.CompletedTask;
        }
    }
}