using EasyCaching.Core;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

namespace MethodCache.Benchmarks.Comparison.Adapters;

/// <summary>
/// Adapter for EasyCaching
/// </summary>
public class EasyCachingAdapter : ICacheAdapter
{
    private readonly IEasyCachingProvider _cache;
    private readonly CacheStatistics _stats = new();
    private readonly IServiceProvider _serviceProvider;

    public string Name => "EasyCaching";

    public EasyCachingAdapter()
    {
        var services = new ServiceCollection();
        services.AddEasyCaching(options =>
        {
            options.UseInMemory("default");
        });
        _serviceProvider = services.BuildServiceProvider();
        var factory = _serviceProvider.GetRequiredService<IEasyCachingProviderFactory>();
        _cache = factory.GetCachingProvider("default");
    }

    public async Task<TValue> GetOrSetAsync<TValue>(
        string key,
        Func<Task<TValue>> factory,
        TimeSpan duration)
    {
        var result = await _cache.GetAsync<TValue>(
            key,
            async () =>
            {
                _stats.Misses++;
                var sw = Stopwatch.StartNew();
                var value = await factory();
                sw.Stop();
                _stats.FactoryCalls++;
                _stats.TotalFactoryDuration += sw.Elapsed;

                return value;
            },
            duration
        );

        return result.Value;
    }

    public bool TryGet<TValue>(string key, out TValue? value)
    {
        var result = _cache.Get<TValue>(key);
        value = result.Value;
        if (result.HasValue)
            _stats.Hits++;
        else
            _stats.Misses++;
        return result.HasValue;
    }

    public void Set<TValue>(string key, TValue value, TimeSpan duration)
    {
        _cache.Set(key, value, duration);
    }

    public void Remove(string key)
    {
        _cache.Remove(key);
    }

    public void Clear()
    {
        _cache.Flush();
    }

    public CacheStatistics GetStatistics() => _stats;

    public void Dispose()
    {
        (_serviceProvider as IDisposable)?.Dispose();
    }
}
