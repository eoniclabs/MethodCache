using MethodCache.Core;

namespace MethodCache.Benchmarks.Comparison.Services;

/// <summary>
/// Service using MethodCache source generation with FastCache provider (ultra-fast minimal overhead)
/// This combines source generation benefits with the absolute minimal cache storage.
/// Expected performance: ~8-15 ns (potentially faster than AdvancedMemory!)
/// </summary>
public partial class FastCacheCachedService : ICachedDataService
{
    private static readonly SamplePayload _cachedPayload = new() { Id = 1, Name = "Cached", Data = new byte[1024] };

    [Cache(Duration = "00:10:00")]
    public virtual SamplePayload GetData(string key)
    {
        return _cachedPayload;
    }

    [Cache(Duration = "00:10:00")]
    public virtual async Task<SamplePayload> GetDataAsync(string key)
    {
        await Task.CompletedTask;
        return _cachedPayload;
    }
}
