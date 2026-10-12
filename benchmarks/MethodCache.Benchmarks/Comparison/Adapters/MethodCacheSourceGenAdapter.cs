using MethodCache.Benchmarks.Comparison.Services;
using MethodCache.Core.Storage.Abstractions;

namespace MethodCache.Benchmarks.Comparison.Adapters;

/// <summary>
/// Adapter for MethodCache using source generation - this is how MethodCache should actually be used!
/// Uses source-generated caching implementation for optimal performance.
/// </summary>
public class MethodCacheSourceGenAdapter : ICacheAdapter
{
    private readonly IMethodCacheBenchmarkService _service;
    private readonly CacheStatistics _stats = new();
    private readonly ICacheManager _cacheManager;
    private readonly IMemoryCache _memoryCache;

    // The [Cache(Duration = "00:10:00")] on IMethodCacheBenchmarkService.GetOrCreateValueAsync
    private static readonly TimeSpan GeneratedDuration = TimeSpan.FromMinutes(10);

    public MethodCacheSourceGenAdapter(IMethodCacheBenchmarkService service, ICacheManager cacheManager)
    {
        _service = service;
        _cacheManager = cacheManager;
        _memoryCache = (IMemoryCache)cacheManager;
    }

    public string Name => "MethodCache (SourceGen)";

    public bool TryGet<TValue>(string key, out TValue? value)
    {
        var found = _cacheManager.TryGetFast(key, out value);
        if (found) _stats.Hits++;
        else _stats.Misses++;
        return found;
    }

    /// <summary>
    /// Direct async cache hit test - calls GetAsync which uses source-generated caching
    /// This properly tests cache hit performance without factory execution
    /// </summary>
    public async Task<SamplePayload> GetAsyncDirect(string key)
    {
        return await _service.GetAsync(key);
    }

    public async Task<TValue> GetOrSetAsync<TValue>(string key, Func<Task<TValue>> factory, TimeSpan duration)
    {
        var loaded = false;
        var result = await _service.GetOrCreateValueAsync(key, async () =>
        {
            loaded = true;
            _stats.Misses++;
            _stats.FactoryCalls++;
            return await factory();
        });
        // The generated decorator already stored the value with its ten-minute policy. Store it again only when
        // the caller asked for another duration, so the benchmarks' ten-minute misses write once.
        if (!loaded) _stats.Hits++;
        else if (duration != GeneratedDuration) await _memoryCache.SetAsync(key, result, duration);
        return result;
    }

    public TValue? GetOrCreate<TValue>(string key, Func<TValue> factory)
    {
        return GetOrSetAsync(key, () => Task.FromResult(factory()), TimeSpan.FromMinutes(10)).GetAwaiter().GetResult();
    }

    public void Set<TValue>(string key, TValue value, TimeSpan duration)
    {
        _memoryCache.SetAsync(key, value, duration).GetAwaiter().GetResult();
    }

    public void Remove(string key) => _memoryCache.RemoveAsync(key).GetAwaiter().GetResult();

    public void Clear() => _memoryCache.ClearAsync().GetAwaiter().GetResult();

    public CacheStatistics GetStatistics() => _stats;

    public void Dispose()
    {
        // Nothing to dispose
    }
}
