using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Caching.Memory;

namespace MethodCache.Benchmarks.Microbenchmarks;

// Separate the cost of a ready key, an application-built key and creating a Task result.
// These references do not claim to provide MethodCache's admission, frequency or invalidation behavior.
[MemoryDiagnoser]
public class MemoryCacheLookupReferenceBenchmarks
{
    private MemoryCache _cache = null!;
    private static readonly Guid Id = new("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
    private readonly string _readyKey = $"user:{Id}";
    private Guid _id = Id;
    public bool SelectPerformanceCores { get; set; } = true;

    [GlobalSetup]
    public void Setup()
    {
        if (SelectPerformanceCores) PrefixBenchmarkSetup.PreferPerformanceCores();
        _cache = new MemoryCache(new MemoryCacheOptions());
        _cache.Set(_readyKey, 42, TimeSpan.FromHours(1));
    }

    [Benchmark] public int ReadyKeySync() => _cache.Get<int>(_readyKey);
    [Benchmark] public int BuiltKeySync() => _cache.Get<int>($"user:{_id}");
    [Benchmark] public Task<int> ReadyKeyTask() => Task.FromResult(_cache.Get<int>(_readyKey));
    [Benchmark] public Task<int> BuiltKeyTask() => Task.FromResult(_cache.Get<int>($"user:{_id}"));

    [GlobalCleanup] public void Cleanup() => _cache.Dispose();
}
