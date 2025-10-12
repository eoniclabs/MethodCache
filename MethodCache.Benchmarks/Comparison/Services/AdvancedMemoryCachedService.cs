using MethodCache.Core;
using MethodCache.Benchmarks.Comparison;

namespace MethodCache.Benchmarks.Comparison.Services;

/// <summary>
/// MethodCache service using AdvancedMemoryStorage provider
/// This tests the sophisticated Memory provider with tag support and statistics
/// </summary>
public partial class AdvancedMemoryCachedService : ICachedDataService
{
    private static readonly SamplePayload _cachedPayload = new()
    {
        Id = 1,
        Name = "Test",
        Data = new byte[1024]
    };

    [Cache(Duration = "00:10:00", Tags = new[] { "test" })]
    public virtual async Task<SamplePayload> GetDataAsync(string key)
    {
        await Task.CompletedTask;
        return _cachedPayload;
    }

    [Cache(Duration = "00:10:00", Tags = new[] { "test" })]
    public virtual SamplePayload GetData(string key)
    {
        return _cachedPayload;
    }
}
