namespace MethodCache.Benchmarks.Comparison.Services;

/// <summary>
/// Interface for MethodCache benchmark with source generation
/// The source generator will create a decorator implementation for this interface
/// </summary>
public interface IMethodCacheBenchmarkService
{
    [Cache(Duration = "00:10:00")]
    Task<SamplePayload> GetAsync([CacheKey(UseAsRawKey = true)] string key);

    [Cache(Duration = "00:10:00")]
    SamplePayload Get([CacheKey(UseAsRawKey = true)] string key);

    [Cache(Duration = "00:10:00")]
    Task<SamplePayload> GetOrCreateAsync([CacheKey(UseAsRawKey = true)] string key);

    [Cache(Duration = "00:10:00")]
    SamplePayload GetOrCreate([CacheKey(UseAsRawKey = true)] string key);

    [Cache(Duration = "00:10:00")]
    Task<T> GetOrCreateValueAsync<T>([CacheKey(UseAsRawKey = true)] string key, Func<Task<T>> factory);

    void Set(string key, SamplePayload value);
    void Remove(string key);
}
