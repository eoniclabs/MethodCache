using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MethodCache.Benchmarks.Comparison.Adapters;

namespace MethodCache.Benchmarks.Comparison;

/// <summary>
/// Quick focused test comparing MethodCache adapter variants
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3, invocationCount: 1000)]
public class QuickMethodCacheTest
{
    private const string TestKey = "test_key";
    private static readonly SamplePayload TestPayload = new() { Id = 1, Name = "Test", Data = new byte[1024] };

    private ICacheAdapter _original = null!;
    private ICacheAdapter _fixedSimple = null!;
    private ICacheAdapter _optimized = null!;
    private ICacheAdapter _baseline = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _original = new MethodCacheAdapter();
        _fixedSimple = new SimpleFixedMethodCacheAdapter();
        _optimized = new ProperlyOptimizedMethodCacheAdapter();
        _baseline = new MemoryCacheAdapter();

        WarmupCaches();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        WarmupCaches();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _original?.Dispose();
        _fixedSimple?.Dispose();
        _optimized?.Dispose();
        _baseline?.Dispose();
    }

    private void WarmupCaches()
    {
        var duration = TimeSpan.FromMinutes(10);
        _original.Set(TestKey, TestPayload, duration);
        _fixedSimple.Set(TestKey, TestPayload, duration);
        _optimized.Set(TestKey, TestPayload, duration);
        _baseline.Set(TestKey, TestPayload, duration);
    }

    [Benchmark]
    public bool Original_Hit()
    {
        return _original.TryGet<SamplePayload>(TestKey, out _);
    }

    [Benchmark]
    public bool FixedSimple_Hit()
    {
        return _fixedSimple.TryGet<SamplePayload>(TestKey, out _);
    }

    [Benchmark]
    public bool Optimized_Hit()
    {
        return _optimized.TryGet<SamplePayload>(TestKey, out _);
    }

    [Benchmark(Baseline = true)]
    public bool Baseline_Hit()
    {
        return _baseline.TryGet<SamplePayload>(TestKey, out _);
    }
}
