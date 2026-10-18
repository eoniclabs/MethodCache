using System.ComponentModel;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using ZiggyCreatures.Caching.Fusion;

namespace MethodCache.Benchmarks.Comparison;

// The benchmark uses int/Guid ids and a string name: both closed payload types are immutable.
// HybridCache requires this declaration to reuse instances rather than deserialize each hit.
[ImmutableObject(true)]
public sealed record BenchUser<TId>(TId Id, string Name);

public interface IReadmeUserService
{
    [Cache(Duration = "00:10:00")]
    Task<BenchUser<int>> GetUserAsync(int id);
}

// The same method keyed by GUID. The interface name has the same length, so both keys have the same prefix length.
public interface IReadmeGuidService
{
    [Cache(Duration = "00:10:00")]
    Task<BenchUser<Guid>> GetUserAsync(Guid id);
}

public sealed class ReadmeUserService : IReadmeUserService, IReadmeGuidService
{
    public Task<BenchUser<int>> GetUserAsync(int id) => Task.FromResult(new BenchUser<int>(id, "Ada"));
    public Task<BenchUser<Guid>> GetUserAsync(Guid id) => Task.FromResult(new BenchUser<Guid>(id, "Ada"));
}

/// <summary>
/// The numbers in the README: what an application pays to cache <c>GetUserAsync(int id)</c> and
/// <c>GetUserAsync(Guid id)</c> with each approach, written the way each library's docs show it and with every library
/// on its default settings.
/// Run: <c>dotnet run -c Release --project MethodCache.Benchmarks -- readme</c>
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ReadmeComparisonBenchmarks
{
    private const int HitId = 42;
    private static readonly Guid HitGuid = new("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(10);

    private readonly ReadmeUserService _source = new();
    private readonly Func<int, Task<BenchUser<int>>> _loadById;
    private readonly Func<Guid, Task<BenchUser<Guid>>> _loadByGuid;
    private IReadmeUserService _methodCache = null!;
    private IReadmeGuidService _methodCacheByGuid = null!;
    private IMemoryCache _memoryCache = null!;
    private HybridCache _hybridCache = null!;
    private FusionCache _fusionCache = null!;
    private ServiceProvider _provider = null!;
    private int _missId;

    public ReadmeComparisonBenchmarks()
    {
        _loadById = _source.GetUserAsync;
        _loadByGuid = _source.GetUserAsync;
    }

    [GlobalSetup]
    public async Task Setup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReadmeUserService, ReadmeUserService>();
        services.AddSingleton<IReadmeGuidService, ReadmeUserService>();
        services.AddMethodCache(b => b.FromAssemblyOf<ReadmeComparisonBenchmarks>().AutoRegister(ServiceLifetime.Singleton));
        services.AddHybridCache();
        _provider = services.BuildServiceProvider();

        _methodCache = _provider.GetRequiredService<IReadmeUserService>();
        _methodCacheByGuid = _provider.GetRequiredService<IReadmeGuidService>();
        // Its own instance: HybridCache stores its wrappers in the registered IMemoryCache under the same keys
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        _hybridCache = _provider.GetRequiredService<HybridCache>();
        _fusionCache = new FusionCache(new FusionCacheOptions());

        await MethodCacheHit();
        await MemoryCacheHit();
        await HybridCacheHit();
        await FusionCacheHit();
        await MethodCacheGuidHit();
        await MemoryCacheGuidHit();
        await HybridCacheGuidHit();
        await FusionCacheGuidHit();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _fusionCache.Dispose();
        _memoryCache.Dispose();
        await _provider.DisposeAsync();
    }

    [BenchmarkCategory("Hit"), Benchmark(Baseline = true, Description = "IMemoryCache by hand")]
    public Task<BenchUser<int>> MemoryCacheHit() => GetWithMemoryCache(HitId, _loadById);

    [BenchmarkCategory("Hit"), Benchmark(Description = "MethodCache [Cache]")]
    public Task<BenchUser<int>> MethodCacheHit() => _methodCache.GetUserAsync(HitId);

    [BenchmarkCategory("Hit"), Benchmark(Description = "HybridCache")]
    public ValueTask<BenchUser<int>> HybridCacheHit() => GetWithHybridCache(HitId, _loadById);

    [BenchmarkCategory("Hit"), Benchmark(Description = "FusionCache")]
    public ValueTask<BenchUser<int>> FusionCacheHit() => GetWithFusionCache(HitId, _loadById);

    [BenchmarkCategory("GuidHit"), Benchmark(Baseline = true, Description = "IMemoryCache by hand")]
    public Task<BenchUser<Guid>> MemoryCacheGuidHit() => GetWithMemoryCache(HitGuid, _loadByGuid);

    [BenchmarkCategory("GuidHit"), Benchmark(Description = "MethodCache [Cache]")]
    public Task<BenchUser<Guid>> MethodCacheGuidHit() => _methodCacheByGuid.GetUserAsync(HitGuid);

    [BenchmarkCategory("GuidHit"), Benchmark(Description = "HybridCache")]
    public ValueTask<BenchUser<Guid>> HybridCacheGuidHit() => GetWithHybridCache(HitGuid, _loadByGuid);

    [BenchmarkCategory("GuidHit"), Benchmark(Description = "FusionCache")]
    public ValueTask<BenchUser<Guid>> FusionCacheGuidHit() => GetWithFusionCache(HitGuid, _loadByGuid);

    // A new key every call: the cost of a miss, storing the result included
    [BenchmarkCategory("Miss"), Benchmark(Baseline = true, Description = "IMemoryCache by hand")]
    public Task<BenchUser<int>> MemoryCacheMiss() => GetWithMemoryCache(NextMissId(), _loadById);

    [BenchmarkCategory("Miss"), Benchmark(Description = "MethodCache [Cache]")]
    public Task<BenchUser<int>> MethodCacheMiss() => _methodCache.GetUserAsync(NextMissId());

    [BenchmarkCategory("Miss"), Benchmark(Description = "HybridCache")]
    public ValueTask<BenchUser<int>> HybridCacheMiss() => GetWithHybridCache(NextMissId(), _loadById);

    [BenchmarkCategory("Miss"), Benchmark(Description = "FusionCache")]
    public ValueTask<BenchUser<int>> FusionCacheMiss() => GetWithFusionCache(NextMissId(), _loadById);

    [BenchmarkCategory("GuidMiss"), Benchmark(Baseline = true, Description = "IMemoryCache by hand")]
    public Task<BenchUser<Guid>> MemoryCacheGuidMiss() => GetWithMemoryCache(NextMissGuid(), _loadByGuid);

    [BenchmarkCategory("GuidMiss"), Benchmark(Description = "MethodCache [Cache]")]
    public Task<BenchUser<Guid>> MethodCacheGuidMiss() => _methodCacheByGuid.GetUserAsync(NextMissGuid());

    [BenchmarkCategory("GuidMiss"), Benchmark(Description = "HybridCache")]
    public ValueTask<BenchUser<Guid>> HybridCacheGuidMiss() => GetWithHybridCache(NextMissGuid(), _loadByGuid);

    [BenchmarkCategory("GuidMiss"), Benchmark(Description = "FusionCache")]
    public ValueTask<BenchUser<Guid>> FusionCacheGuidMiss() => GetWithFusionCache(NextMissGuid(), _loadByGuid);

    private int NextMissId() => 1_000_000 + Interlocked.Increment(ref _missId);

    // A distinct GUID per call, without adding Guid.NewGuid's random-number cost to every library's miss
    private Guid NextMissGuid() => new(Interlocked.Increment(ref _missId), 0x4d43, 0x4000, 0x80, 0, 0, 0, 0, 0, 0, 0);

    private async Task<BenchUser<TId>> GetWithMemoryCache<TId>(TId id, Func<TId, Task<BenchUser<TId>>> load)
    {
        var key = $"user:{id}";
        if (_memoryCache.TryGetValue(key, out BenchUser<TId>? user))
        {
            return user!;
        }

        user = await load(id);
        _memoryCache.Set(key, user, Duration);
        return user;
    }

    private ValueTask<BenchUser<TId>> GetWithHybridCache<TId>(TId id, Func<TId, Task<BenchUser<TId>>> load) =>
        _hybridCache.GetOrCreateAsync(
            $"user:{id}",
            (load, id),
            static async (state, _) => await state.load(state.id),
            new HybridCacheEntryOptions { Expiration = Duration, LocalCacheExpiration = Duration });

    private ValueTask<BenchUser<TId>> GetWithFusionCache<TId>(TId id, Func<TId, Task<BenchUser<TId>>> load) =>
        _fusionCache.GetOrSetAsync<BenchUser<TId>>(
            $"user:{id}",
            async (_, _) => await load(id),
            options => options.SetDuration(Duration));
}
