using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Runtime.KeyGeneration;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

namespace MethodCache.Benchmarks.Comparison.Adapters;

/// <summary>
/// Adapter for MethodCache (using direct ICacheManager for fair comparison)
/// </summary>
public class MethodCacheAdapter : ICacheAdapter
{
    private readonly ICacheManager _cacheManager;
    private readonly ICacheKeyGenerator _keyGenerator;
    private readonly IServiceProvider _serviceProvider;
    private readonly CacheStatistics _stats = new();

    public string Name => "MethodCache (Legacy Runtime)";

    public MethodCacheAdapter()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMethodCache(config =>
        {
            config.DefaultPolicy(builder => builder.WithDuration(TimeSpan.FromMinutes(10)));
        }); // Don't scan assembly to avoid conflicts

        _serviceProvider = services.BuildServiceProvider();
        _cacheManager = _serviceProvider.GetRequiredService<ICacheManager>();
        _keyGenerator = _serviceProvider.GetRequiredService<ICacheKeyGenerator>();
    }

    public async Task<TValue> GetOrSetAsync<TValue>(
        string key,
        Func<Task<TValue>> factory,
        TimeSpan duration)
    {
        var descriptor = CacheRuntimePolicy.FromPolicy(
            key,
            CachePolicy.Empty with { Duration = duration },
            CachePolicyFields.Duration
        );

        return await _cacheManager.GetOrCreateAsync<TValue>(
            key,
            Array.Empty<object>(),
            async () =>
            {
                _stats.Misses++;
                var sw = Stopwatch.StartNew();
                var result = await factory();
                sw.Stop();
                _stats.FactoryCalls++;
                _stats.TotalFactoryDuration += sw.Elapsed;

                return result;
            },
            descriptor,
            _keyGenerator
        );
    }

    public bool TryGet<TValue>(string key, out TValue? value)
    {
        var descriptor = CacheRuntimePolicy.FromPolicy(
            key,
            CachePolicy.Empty,
            CachePolicyFields.None
        );

        var task = _cacheManager.TryGetAsync<TValue>(key, Array.Empty<object>(), descriptor, _keyGenerator);
        task.AsTask().Wait();
        value = task.Result;

        var found = value != null;
        if (found)
            _stats.Hits++;
        else
            _stats.Misses++;
        return found;
    }

    public void Set<TValue>(string key, TValue value, TimeSpan duration)
    {
        // MethodCache doesn't expose a direct Set method
        // We'll use GetOrCreateAsync with a factory that returns the value
        var descriptor = CacheRuntimePolicy.FromPolicy(
            key,
            CachePolicy.Empty with { Duration = duration },
            CachePolicyFields.Duration
        );

        var task = _cacheManager.GetOrCreateAsync<TValue>(
            key,
            Array.Empty<object>(),
            () => Task.FromResult(value),
            descriptor,
            _keyGenerator
        );
        task.Wait();
    }

    public void Remove(string key)
    {
        _cacheManager.InvalidateByKeysAsync(key).Wait();
    }

    public void Clear()
    {
        // MethodCache doesn't have a clear all method exposed
        // Would need to track keys
    }

    public CacheStatistics GetStatistics() => _stats;

    public void Dispose()
    {
        (_serviceProvider as IDisposable)?.Dispose();
    }
}
