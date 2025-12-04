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

    /// <summary>
    /// Optional factory function for GetOrCreateAsync/GetOrCreate methods.
    /// When null, returns static payload immediately (for cache hit benchmarks).
    /// When set, executes the factory (for cache miss benchmarks with realistic delays).
    /// </summary>
    public Func<string, Task<SamplePayload>>? Factory { get; set; }

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
        // If a factory is configured (for MissAndSet benchmarks), use it
        var factory = Factory;
        if (factory != null)
        {
            return await factory(key);
        }

        // Otherwise return immediately (for cache hit benchmarks)
        await Task.CompletedTask;
        return _payload;
    }

    public SamplePayload GetOrCreate(string key)
    {
        // If a factory is configured (for MissAndSet benchmarks), use it
        var factory = Factory;
        if (factory != null)
        {
            return factory(key).GetAwaiter().GetResult();
        }

        // Otherwise return immediately (for cache hit benchmarks)
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
