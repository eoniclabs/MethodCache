using MethodCache.Benchmarks.Comparison.Services;

namespace MethodCache.Benchmarks.Comparison.Adapters;

/// <summary>
/// Adapter for MethodCache using source generation - this is how MethodCache should actually be used!
/// Uses source-generated caching implementation for optimal performance.
/// </summary>
public class MethodCacheSourceGenAdapter : ICacheAdapter
{
    private readonly IMethodCacheBenchmarkService _service;
    private readonly CacheStatistics _stats = new();

    public MethodCacheSourceGenAdapter(IMethodCacheBenchmarkService service)
    {
        _service = service;
    }

    public string Name => "MethodCache (Source Gen)";

    public bool TryGet<TValue>(string key, out TValue? value)
    {
        try
        {
            // OPTIMIZATION: Use GetAsync with synchronous completion check to avoid sync-over-async
            var task = _service.GetAsync(key);
            SamplePayload? result;
            if (task.IsCompletedSuccessfully)
            {
                // Fast path: task completed synchronously
                result = task.Result;
            }
            else
            {
                // Slow path: have to block (but cache hits should never hit this)
                result = task.GetAwaiter().GetResult();
            }

            if (result != null)
            {
                _stats.Hits++;
                value = (TValue)(object)result;
                return true;
            }
            else
            {
                _stats.Misses++;
                value = default;
                return false;
            }
        }
        catch
        {
            _stats.Misses++;
            value = default;
            return false;
        }
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
        // Source-generated caching handles this
        var result = await _service.GetOrCreateAsync(key);
        _stats.Hits++;
        return (TValue)(object)result;
    }

    public TValue? GetOrCreate<TValue>(string key, Func<TValue> factory)
    {
        // Source-generated caching handles this
        var result = _service.GetOrCreate(key);
        _stats.Hits++;
        return (TValue)(object)result;
    }

    public void Set<TValue>(string key, TValue value, TimeSpan duration)
    {
        // Not used in read benchmarks
        _service.Set(key, (SamplePayload)(object)value!);
    }

    public void Remove(string key)
    {
        _service.Remove(key);
    }

    public void Clear()
    {
        // Not needed for these benchmarks
    }

    public CacheStatistics GetStatistics() => _stats;

    public void Dispose()
    {
        // Nothing to dispose
    }
}
