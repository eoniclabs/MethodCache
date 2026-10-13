using System.Runtime.CompilerServices;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Benchmarks.Comparison.Adapters;

/// <summary>
/// MethodCache adapter with a prefix generated once during setup and the user key appended per operation.
/// All operations share this identity. This measures a manual precomputed-prefix approach;
/// the source-generator adapter exercises generated decorators separately.
/// </summary>
public class StaticKeyMethodCacheAdapter : ICacheAdapter
{
    private readonly IMemoryCache _memoryCache;
    private readonly IServiceProvider _serviceProvider;
    private readonly CacheStatistics _stats = new();

    private readonly ICacheManager _cacheManager;
    private readonly string _keyPrefix;

    public string Name => "StaticKeyMethodCache";

    public StaticKeyMethodCacheAdapter(TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (timeProvider != null) services.AddSingleton(timeProvider);
        services.AddMethodCache(config =>
        {
            config.DefaultPolicy(builder => builder.WithDuration(TimeSpan.FromMinutes(10)));
        }); // Don't scan assembly to avoid conflicts

        _serviceProvider = services.BuildServiceProvider();

        // Get ICacheManager and cast to IMemoryCache
        _cacheManager = _serviceProvider.GetRequiredService<ICacheManager>();
        _memoryCache = (IMemoryCache)_cacheManager;

        // Generate a storage-key prefix once during setup.
        var keyGenerator = _serviceProvider.GetRequiredService<ICacheKeyGenerator>();
        var defaultPolicy = CacheRuntimePolicy.FromPolicy(
            "default",
            CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(10) },
            CachePolicyFields.Duration
        );

        _keyPrefix = keyGenerator.GenerateKey("StaticKey", Array.Empty<object>(), defaultPolicy);
    }

    public async Task<TValue> GetOrSetAsync<TValue>(
        string key,
        Func<Task<TValue>> factory,
        TimeSpan duration)
    {
        // Append the user key to the precomputed prefix.
        var cacheKey = $"{_keyPrefix}:{key}";

        if (_cacheManager.TryGetFast<TValue>(cacheKey, out var cached))
        {
            _stats.Hits++;
            return cached!;
        }

        // Cache miss - execute factory
        _stats.Misses++;
        _stats.FactoryCalls++;
        var result = await factory();

        // Set in cache - direct access with static key
        await _memoryCache.SetAsync(cacheKey, result, duration);

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet<TValue>(string key, out TValue? value)
    {
        // Use pre-generated static key - NO runtime key generation!
        var cacheKey = $"{_keyPrefix}:{key}";

        var found = _cacheManager.TryGetFast(cacheKey, out value);

        if (found)
            _stats.Hits++;
        else
            _stats.Misses++;

        return found;
    }

    public void Set<TValue>(string key, TValue value, TimeSpan duration)
    {
        // Construct the storage key from the precomputed prefix.
        var cacheKey = $"{_keyPrefix}:{key}";

        var valueTask = _memoryCache.SetAsync(cacheKey, value, duration);

        if (!valueTask.IsCompleted)
        {
            valueTask.GetAwaiter().GetResult();
        }
    }

    public void Remove(string key)
    {
        var cacheKey = $"{_keyPrefix}:{key}";
        var valueTask = _memoryCache.RemoveAsync(cacheKey);
        if (!valueTask.IsCompleted)
        {
            valueTask.GetAwaiter().GetResult();
        }
    }

    public void Clear()
    {
        var valueTask = _memoryCache.ClearAsync();
        if (!valueTask.IsCompleted)
        {
            valueTask.GetAwaiter().GetResult();
        }
    }

    public CacheStatistics GetStatistics() => _stats;

    public void Dispose()
    {
        (_serviceProvider as IDisposable)?.Dispose();
    }
}
