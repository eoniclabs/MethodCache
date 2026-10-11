using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BitFaster.Caching;
using BitFaster.Caching.Lfu;
using MethodCache.Benchmarks.Microbenchmarks.Legacy;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using ZiggyCreatures.Caching.Fusion;

using MethodCacheOptions = MethodCache.MethodCacheMemoryOptions;
using MsMemoryCacheOptions = Microsoft.Extensions.Caching.Memory.MemoryCacheOptions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace MethodCache.Benchmarks.Microbenchmarks;

/// <summary>
/// Head-to-head latency of the in-memory engine against the previous engine and other .NET caches.
/// Each benchmark performs <see cref="Operations"/> operations over distinct keys.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class MemoryEngineBenchmarks
{
    public const int Operations = 1024;
    private const int Capacity = 10_000;

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private readonly CacheRuntimePolicy _policy = CacheRuntimePolicy.Empty("Bench") with { Duration = Ttl };
    private readonly Func<Task<string>> _factory = static () => Task.FromResult("value");

    private string[] _hitKeys = null!;
    private string[] _missKeys = null!;
    private int _missOffset;

    private InMemoryCacheManager _engine = null!;
    private LegacyInMemoryCacheManager _legacy = null!;
    private MemoryCache _memoryCache = null!;
    private ICache<string, string> _bitFaster = null!;
    private IAsyncCache<string, string> _bitFasterAsync = null!;
    private FusionCache _fusion = null!;
    private HybridCache _hybrid = null!;
    private ServiceProvider _services = null!;

    [GlobalSetup]
    public void Setup()
    {
        _hitKeys = Enumerable.Range(0, Operations).Select(i => $"user:{i}:profile").ToArray();
        _missKeys = Enumerable.Range(0, 2_000_000).Select(i => $"miss:{i}").ToArray();

        _engine = new InMemoryCacheManager(new NullCacheMetricsProvider(), MsOptions.Create(new MethodCacheOptions { MaxItems = Capacity }));
        _legacy = new LegacyInMemoryCacheManager(new NullCacheMetricsProvider(), MsOptions.Create(new MethodCacheOptions { MaxItems = Capacity, EvictionPolicy = global::MethodCache.MemoryCacheEvictionPolicy.Lru }));
        _memoryCache = new MemoryCache(new MsMemoryCacheOptions { SizeLimit = Capacity });
        _bitFaster = new ConcurrentLfuBuilder<string, string>().WithCapacity(Capacity).WithExpireAfterWrite(Ttl).Build();
        _bitFasterAsync = new ConcurrentLfuBuilder<string, string>().WithCapacity(Capacity).WithExpireAfterWrite(Ttl).AsAsyncCache().Build();
        _fusion = new FusionCache(new FusionCacheOptions { DefaultEntryOptions = new FusionCacheEntryOptions { Duration = Ttl } });

        _services = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider();
        _hybrid = _services.GetRequiredService<HybridCache>();

        // Fill every cache to capacity so the write benchmarks always evict.
        for (var i = 0; i < Capacity; i++)
        {
            var key = i < Operations ? _hitKeys[i] : $"fill:{i}";
            _engine.SetAsync(key, "value", Ttl).GetAwaiter().GetResult();
            _legacy.SetAsync(key, "value", Ttl).GetAwaiter().GetResult();
            _memoryCache.Set(key, "value", new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = Ttl });
            _bitFaster.AddOrUpdate(key, "value");
            _bitFasterAsync.AddOrUpdate(key, "value");
            _fusion.Set(key, "value");
            if (i < Operations)
            {
                _hybrid.SetAsync(key, "value").AsTask().GetAwaiter().GetResult();
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _engine.Dispose();
        _legacy.Dispose();
        _memoryCache.Dispose();
        _fusion.Dispose();
        _services.Dispose();
    }

    // ---------------------------------------------------------------- synchronous hit

    [Benchmark(Baseline = true, OperationsPerInvoke = Operations), BenchmarkCategory("1 Hit (sync)")]
    public int MethodCache_TryGetFast()
    {
        var found = 0;
        foreach (var key in _hitKeys)
        {
            if (_engine.TryGetFast<string>(key, out _)) found++;
        }

        return found;
    }

    [Benchmark(OperationsPerInvoke = Operations), BenchmarkCategory("1 Hit (sync)")]
    public int Legacy_TryGetFast()
    {
        var found = 0;
        foreach (var key in _hitKeys)
        {
            if (_legacy.TryGetFast<string>(key, out _)) found++;
        }

        return found;
    }

    [Benchmark(OperationsPerInvoke = Operations), BenchmarkCategory("1 Hit (sync)")]
    public int MemoryCache_TryGetValue()
    {
        var found = 0;
        foreach (var key in _hitKeys)
        {
            if (_memoryCache.TryGetValue(key, out _)) found++;
        }

        return found;
    }

    [Benchmark(OperationsPerInvoke = Operations), BenchmarkCategory("1 Hit (sync)")]
    public int BitFaster_TryGet()
    {
        var found = 0;
        foreach (var key in _hitKeys)
        {
            if (_bitFaster.TryGet(key, out _)) found++;
        }

        return found;
    }

    [Benchmark(OperationsPerInvoke = Operations), BenchmarkCategory("1 Hit (sync)")]
    public int FusionCache_TryGet()
    {
        var found = 0;
        foreach (var key in _hitKeys)
        {
            if (_fusion.TryGet<string>(key).HasValue) found++;
        }

        return found;
    }

    // ---------------------------------------------------------------- get-or-create hit

    [Benchmark(OperationsPerInvoke = Operations), BenchmarkCategory("2 GetOrCreate hit (async)")]
    public async Task<int> MethodCache_GetOrCreate()
    {
        var length = 0;
        foreach (var key in _hitKeys)
        {
            length += (await _engine.GetOrCreateFastAsync(key, "Bench", _factory, _policy)).Length;
        }

        return length;
    }

    [Benchmark(OperationsPerInvoke = Operations), BenchmarkCategory("2 GetOrCreate hit (async)")]
    public async Task<int> Legacy_GetOrCreate()
    {
        var length = 0;
        foreach (var key in _hitKeys)
        {
            length += (await _legacy.GetOrCreateFastAsync(key, "Bench", _factory, _policy)).Length;
        }

        return length;
    }

    [Benchmark(OperationsPerInvoke = Operations), BenchmarkCategory("2 GetOrCreate hit (async)")]
    public async Task<int> MemoryCache_GetOrCreate()
    {
        var length = 0;
        foreach (var key in _hitKeys)
        {
            length += (await _memoryCache.GetOrCreateAsync(key, entry =>
            {
                entry.Size = 1;
                entry.AbsoluteExpirationRelativeToNow = Ttl;
                return _factory();
            }))!.Length;
        }

        return length;
    }

    [Benchmark(OperationsPerInvoke = Operations), BenchmarkCategory("2 GetOrCreate hit (async)")]
    public async Task<int> BitFaster_GetOrAdd()
    {
        var length = 0;
        foreach (var key in _hitKeys)
        {
            length += (await _bitFasterAsync.GetOrAddAsync(key, static _ => Task.FromResult("value"))).Length;
        }

        return length;
    }

    [Benchmark(OperationsPerInvoke = Operations), BenchmarkCategory("2 GetOrCreate hit (async)")]
    public async Task<int> FusionCache_GetOrSet()
    {
        var length = 0;
        foreach (var key in _hitKeys)
        {
            length += (await _fusion.GetOrSetAsync<string>(key, static (_, _) => Task.FromResult("value"))).Length;
        }

        return length;
    }

    [Benchmark(OperationsPerInvoke = Operations), BenchmarkCategory("2 GetOrCreate hit (async)")]
    public async Task<int> HybridCache_GetOrCreate()
    {
        var length = 0;
        foreach (var key in _hitKeys)
        {
            length += (await _hybrid.GetOrCreateAsync(key, static _ => ValueTask.FromResult("value"))).Length;
        }

        return length;
    }

    // ---------------------------------------------------------------- write into a full cache

    private ArraySegment<string> NextMissKeys()
    {
        var offset = Interlocked.Add(ref _missOffset, Operations) % (_missKeys.Length - Operations);
        return new ArraySegment<string>(_missKeys, offset, Operations);
    }

    [Benchmark(OperationsPerInvoke = Operations), BenchmarkCategory("3 Insert with eviction")]
    public async Task MethodCache_Set()
    {
        foreach (var key in NextMissKeys())
        {
            await _engine.SetAsync(key, "value", Ttl);
        }
    }

    [Benchmark(OperationsPerInvoke = Operations), BenchmarkCategory("3 Insert with eviction")]
    public async Task Legacy_Set()
    {
        foreach (var key in NextMissKeys())
        {
            await _legacy.SetAsync(key, "value", Ttl);
        }
    }

    [Benchmark(OperationsPerInvoke = Operations), BenchmarkCategory("3 Insert with eviction")]
    public void MemoryCache_Set()
    {
        foreach (var key in NextMissKeys())
        {
            _memoryCache.Set(key, "value", new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = Ttl });
        }
    }

    [Benchmark(OperationsPerInvoke = Operations), BenchmarkCategory("3 Insert with eviction")]
    public void BitFaster_AddOrUpdate()
    {
        foreach (var key in NextMissKeys())
        {
            _bitFaster.AddOrUpdate(key, "value");
        }
    }

    [Benchmark(OperationsPerInvoke = Operations), BenchmarkCategory("3 Insert with eviction")]
    public void FusionCache_Set()
    {
        foreach (var key in NextMissKeys())
        {
            _fusion.Set(key, "value");
        }
    }
}
