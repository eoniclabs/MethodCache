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
            // The source-generated code handles caching internally
            var result = _service.Get(key);
            _stats.Hits++;
            value = (TValue)(object)result!;
            return true;
        }
        catch
        {
            _stats.Misses++;
            value = default;
            return false;
        }
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
