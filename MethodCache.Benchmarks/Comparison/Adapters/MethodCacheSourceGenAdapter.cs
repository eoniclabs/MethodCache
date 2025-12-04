using MethodCache.Benchmarks.Comparison.Services;

namespace MethodCache.Benchmarks.Comparison.Adapters;

/// <summary>
/// Adapter for MethodCache using source generation - this is how MethodCache should actually be used!
/// Uses source-generated caching implementation for optimal performance.
/// </summary>
public class MethodCacheSourceGenAdapter : ICacheAdapter
{
    private readonly IMethodCacheBenchmarkService _service;
    private readonly MethodCacheBenchmarkService _baseService;
    private readonly CacheStatistics _stats = new();

    public MethodCacheSourceGenAdapter(IMethodCacheBenchmarkService service, MethodCacheBenchmarkService baseService)
    {
        _service = service;
        _baseService = baseService;
    }

    public string Name => "MethodCache (SourceGen + AdvancedMemory)";

    public bool TryGet<TValue>(string key, out TValue? value)
    {
        try
        {
            // OPTIMIZATION: Use synchronous Get to avoid Task allocation and sync-over-async overhead
            var result = _service.Get(key);

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
        // Configure the factory on the base service so it gets called on cache miss
        // This ensures fair comparison with other libraries that execute factories
        _baseService.Factory = async (k) => (SamplePayload)(object)(await factory())!;

        try
        {
            // Source-generated caching handles this - will call factory on miss
            var result = await _service.GetOrCreateAsync(key);
            _stats.Hits++;
            return (TValue)(object)result;
        }
        finally
        {
            // Clear factory to avoid affecting cache hit benchmarks
            _baseService.Factory = null;
        }
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
        if (value is not SamplePayload payload)
        {
            throw new InvalidOperationException("MethodCache benchmarks only support SamplePayload values.");
        }

        // Inject a one-time factory so the cache stores the provided payload
        _baseService.Factory = _ => Task.FromResult(payload);
        try
        {
            _service.GetOrCreateAsync(key).GetAwaiter().GetResult();
        }
        finally
        {
            _baseService.Factory = null;
        }
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
