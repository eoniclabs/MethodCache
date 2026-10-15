using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using ZiggyCreatures.Caching.Fusion;

namespace MethodCache.Benchmarks.Comparison;

public sealed record BenchUser(Guid Id, string Name);

public interface IReadmeUserService
{
    [Cache(Duration = "00:10:00")]
    Task<BenchUser> GetUserAsync(Guid id);
}

public sealed class ReadmeUserService : IReadmeUserService
{
    public Task<BenchUser> GetUserAsync(Guid id) => Task.FromResult(new BenchUser(id, "Ada"));
}

/// <summary>
/// The numbers in the README: what an application pays to cache <c>GetUserAsync(Guid id)</c> with each approach,
/// written the way each library's docs show it and with every library on its default settings.
/// Run: <c>dotnet run -c Release --project MethodCache.Benchmarks -- readme</c>
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ReadmeComparisonBenchmarks
{
    private static readonly Guid HitId = new("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(10);

    private readonly ReadmeUserService _source = new();
    private IReadmeUserService _methodCache = null!;
    private IMemoryCache _memoryCache = null!;
    private HybridCache _hybridCache = null!;
    private FusionCache _fusionCache = null!;
    private ServiceProvider _provider = null!;
    private int _missId;

    [GlobalSetup]
    public async Task Setup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReadmeUserService, ReadmeUserService>();
        services.AddMethodCache(b => b.FromAssemblyOf<ReadmeComparisonBenchmarks>().AutoRegister(ServiceLifetime.Singleton));
        services.AddHybridCache();
        _provider = services.BuildServiceProvider();

        _methodCache = _provider.GetRequiredService<IReadmeUserService>();
        // Its own instance: HybridCache stores its wrappers in the registered IMemoryCache under the same keys
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        _hybridCache = _provider.GetRequiredService<HybridCache>();
        _fusionCache = new FusionCache(new FusionCacheOptions());

        await MethodCacheHit();
        await MemoryCacheHit();
        await HybridCacheHit();
        await FusionCacheHit();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _fusionCache.Dispose();
        _memoryCache.Dispose();
        await _provider.DisposeAsync();
    }

    [BenchmarkCategory("Hit"), Benchmark(Baseline = true, Description = "IMemoryCache by hand")]
    public Task<BenchUser> MemoryCacheHit() => GetWithMemoryCache(HitId);

    [BenchmarkCategory("Hit"), Benchmark(Description = "MethodCache [Cache]")]
    public Task<BenchUser> MethodCacheHit() => _methodCache.GetUserAsync(HitId);

    [BenchmarkCategory("Hit"), Benchmark(Description = "HybridCache")]
    public ValueTask<BenchUser> HybridCacheHit() => GetWithHybridCache(HitId);

    [BenchmarkCategory("Hit"), Benchmark(Description = "FusionCache")]
    public ValueTask<BenchUser> FusionCacheHit() => GetWithFusionCache(HitId);

    // A new key every call: the cost of a miss, storing the result included
    [BenchmarkCategory("Miss"), Benchmark(Baseline = true, Description = "IMemoryCache by hand")]
    public Task<BenchUser> MemoryCacheMiss() => GetWithMemoryCache(NextMissId());

    [BenchmarkCategory("Miss"), Benchmark(Description = "MethodCache [Cache]")]
    public Task<BenchUser> MethodCacheMiss() => _methodCache.GetUserAsync(NextMissId());

    [BenchmarkCategory("Miss"), Benchmark(Description = "HybridCache")]
    public ValueTask<BenchUser> HybridCacheMiss() => GetWithHybridCache(NextMissId());

    [BenchmarkCategory("Miss"), Benchmark(Description = "FusionCache")]
    public ValueTask<BenchUser> FusionCacheMiss() => GetWithFusionCache(NextMissId());

    // A distinct GUID per call, without adding Guid.NewGuid's random-number cost to every library's miss
    private Guid NextMissId() => new(Interlocked.Increment(ref _missId), 0x4d43, 0x4000, 0x80, 0, 0, 0, 0, 0, 0, 0);

    private async Task<BenchUser> GetWithMemoryCache(Guid id)
    {
        var key = $"user:{id}";
        if (_memoryCache.TryGetValue(key, out BenchUser? user))
        {
            return user!;
        }

        user = await _source.GetUserAsync(id);
        _memoryCache.Set(key, user, Duration);
        return user;
    }

    private ValueTask<BenchUser> GetWithHybridCache(Guid id) =>
        _hybridCache.GetOrCreateAsync(
            $"user:{id}",
            (_source, id),
            static async (state, _) => await state._source.GetUserAsync(state.id),
            new HybridCacheEntryOptions { Expiration = Duration, LocalCacheExpiration = Duration });

    private ValueTask<BenchUser> GetWithFusionCache(Guid id) =>
        _fusionCache.GetOrSetAsync<BenchUser>(
            $"user:{id}",
            async (_, _) => await _source.GetUserAsync(id),
            options => options.SetDuration(Duration));
}
