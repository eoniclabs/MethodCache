namespace MethodCache.Benchmarks.Comparison.Services;

/// <summary>
/// Interface for cached data service - MethodCache source generator will implement caching
/// </summary>
public interface ICachedDataService
{
    Task<SamplePayload> GetDataAsync(string key);
    SamplePayload GetData(string key);
}
