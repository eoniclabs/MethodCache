namespace MethodCache.Benchmarks.Comparison.Services;

/// <summary>
/// Basic implementation of IMethodCacheBenchmarkService
/// The source generator will create a decorator that wraps this implementation with caching
/// </summary>
public class MethodCacheBenchmarkService : IMethodCacheBenchmarkService
{
    private static readonly SamplePayload _payload = new()
    {
        Id = 1,
        Name = "Cached",
        Data = new byte[1024]
    };

    public async Task<SamplePayload> GetAsync(string key)
    {
        await Task.CompletedTask;
        return _payload;
    }

    public SamplePayload Get(string key)
    {
        return _payload;
    }

    public async Task<SamplePayload> GetOrCreateAsync(string key)
    {
        await Task.CompletedTask;
        return _payload;
    }

    public SamplePayload GetOrCreate(string key)
    {
        return _payload;
    }

    public void Set(string key, SamplePayload value)
    {
        // No-op for benchmark
    }

    public void Remove(string key)
    {
        // No-op for benchmark
    }
}
