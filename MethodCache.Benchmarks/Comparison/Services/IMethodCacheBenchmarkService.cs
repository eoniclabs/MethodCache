using MethodCache.Core;

namespace MethodCache.Benchmarks.Comparison.Services;

/// <summary>
/// Interface for MethodCache benchmark with source generation
/// The source generator will create a decorator implementation for this interface
/// </summary>
public interface IMethodCacheBenchmarkService
{
    [Cache(Duration = "00:10:00")]
    Task<SamplePayload> GetAsync(string key);

    [Cache(Duration = "00:10:00")]
    SamplePayload Get(string key);

    [Cache(Duration = "00:10:00")]
    Task<SamplePayload> GetOrCreateAsync(string key);

    [Cache(Duration = "00:10:00")]
    SamplePayload GetOrCreate(string key);

    void Set(string key, SamplePayload value);
    void Remove(string key);
}
