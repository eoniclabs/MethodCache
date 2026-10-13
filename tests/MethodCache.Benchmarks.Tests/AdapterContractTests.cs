using MethodCache.Benchmarks.Comparison;
using MethodCache.Benchmarks.Comparison.Adapters;
using MethodCache.Benchmarks.Comparison.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Microsoft.Extensions.Time.Testing;

namespace MethodCache.Benchmarks.Tests;

public class AdapterContractTests
{
    public static IEnumerable<object[]> Adapters() => Enumerable.Range(0, 9).Select(i => new object[] { i });
    public static IEnumerable<object[]> GenericAdapters() => Enumerable.Range(0, 9).Select(i => new object[] { i });
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(10);

    [Theory, MemberData(nameof(Adapters))]
    public void SeparateInstances_ClearAndDispose_DoNotAffectAnotherAdapter(int kind)
    {
        using var first = Create(kind);
        using var second = Create(kind);
        var payload = new SamplePayload { Id = 7 };
        second.Adapter.Set("same-key", payload, Duration);
        Assert.False(first.Adapter.TryGet<SamplePayload>("same-key", out _));
        first.Adapter.Clear();
        first.Dispose();
        Assert.True(second.Adapter.TryGet<SamplePayload>("same-key", out var read));
        Assert.Equal(7, read!.Id);
    }

    [Theory]
    [InlineData(0), InlineData(1), InlineData(2), InlineData(3), InlineData(8)]
    public async Task MethodCacheAdapters_RequestedDuration_ExpiresBothSetAndFactoryValues(int kind)
    {
        var clock = new FakeTimeProvider();
        using var scope = Create(kind, clock);
        var payload = new SamplePayload { Id = 1 };
        scope.Adapter.Set("set", payload, TimeSpan.FromSeconds(1));
        await scope.Adapter.GetOrSetAsync("created", () => Task.FromResult(payload), TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(scope.Adapter.TryGet<SamplePayload>("set", out _));
        Assert.False(scope.Adapter.TryGet<SamplePayload>("created", out _));
    }

    [Fact]
    public async Task SourceGenerator_ConcurrentDifferentKeys_KeepTheirOwnFactory()
    {
        using var scope = Create(8);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = scope.Adapter.GetOrSetAsync("first", async () =>
        {
            entered.SetResult();
            await release.Task;
            return new SamplePayload { Id = 1 };
        }, Duration);
        await entered.Task;
        Assert.Equal(2, (await scope.Adapter.GetOrSetAsync("second", () => Task.FromResult(new SamplePayload { Id = 2 }), Duration)).Id);
        release.SetResult();
        Assert.Equal(1, (await first).Id);
        Assert.True(scope.Adapter.TryGet<SamplePayload>("first", out var read));
        Assert.Equal(1, read!.Id);
    }

    [Theory, MemberData(nameof(Adapters))]
    public void Set_ReadOverwriteRemoveAndClear_UseOneIdentity(int kind)
    {
        using var scope = Create(kind);
        var adapter = scope.Adapter;
        var first = new SamplePayload { Id = 1 };
        var second = new SamplePayload { Id = 2 };
        adapter.Set("key", first, Duration);
        Assert.True(adapter.TryGet<SamplePayload>("key", out var read));
        Assert.Equal(first.Id, read!.Id);
        adapter.Set("key", second, Duration);
        Assert.True(adapter.TryGet<SamplePayload>("key", out read));
        Assert.Equal(second.Id, read!.Id);
        adapter.Remove("key");
        Assert.False(adapter.TryGet<SamplePayload>("key", out _));
        adapter.Set("key", first, Duration);
        adapter.Set("other", second, Duration);
        adapter.Clear();
        Assert.False(adapter.TryGet<SamplePayload>("key", out _));
        Assert.False(adapter.TryGet<SamplePayload>("other", out _));
    }

    [Theory, MemberData(nameof(Adapters))]
    public async Task GetOrSet_UsesSetValueAndPublishesToTryGet(int kind)
    {
        using var scope = Create(kind);
        var adapter = scope.Adapter;
        var stored = new SamplePayload { Id = 1 };
        var calls = 0;
        Task<SamplePayload> Factory() { calls++; return Task.FromResult(stored); }
        Assert.Equal(stored.Id, (await adapter.GetOrSetAsync("created", Factory, Duration)).Id);
        Assert.True(adapter.TryGet<SamplePayload>("created", out var read));
        Assert.Equal(stored.Id, read!.Id);
        Assert.Equal(stored.Id, (await adapter.GetOrSetAsync("created", Factory, Duration)).Id);
        adapter.Set("set", stored, Duration);
        Assert.Equal(stored.Id, (await adapter.GetOrSetAsync("set", Factory, Duration)).Id);
        Assert.Equal(1, calls);
    }

    [Theory, MemberData(nameof(GenericAdapters))]
    public async Task ZeroValue_IsAHitAndAbsentValueIsAMiss(int kind)
    {
        using var scope = Create(kind);
        var adapter = scope.Adapter;
        Assert.False(adapter.TryGet<int>("absent", out _));
        adapter.Set("zero", 0, Duration);
        Assert.True(adapter.TryGet<int>("zero", out var read));
        Assert.Equal(0, read);
        var calls = 0;
        Assert.Equal(0, await adapter.GetOrSetAsync("zero", () => { calls++; return Task.FromResult(1); }, Duration));
        Assert.Equal(0, calls);
        adapter.Remove("zero");
        Assert.False(adapter.TryGet<int>("zero", out _));
    }

    private static AdapterScope Create(int kind, TimeProvider? timeProvider = null)
    {
        if (kind != 8)
            return new AdapterScope(kind switch
            {
                0 => new MethodCacheAdapter(timeProvider),
                1 => new OptimizedMethodCacheAdapter(timeProvider),
                2 => new StaticKeyMethodCacheAdapter(timeProvider),
                3 => new DirectApiMethodCacheAdapter(timeProvider),
                4 => new MemoryCacheAdapter(),
                5 => new LazyCacheAdapter(),
                6 => new FusionCacheAdapter(),
                7 => new EasyCachingAdapter(),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            });
        var services = new ServiceCollection();
        services.AddLogging();
        if (timeProvider != null) services.AddSingleton(timeProvider);
        services.AddSingleton<MethodCacheBenchmarkService>();
        services.AddSingleton<IMethodCacheBenchmarkService>(sp => sp.GetRequiredService<MethodCacheBenchmarkService>());
        services.AddMethodCache();
        var provider = services.BuildServiceProvider();
        return new AdapterScope(new MethodCacheSourceGenAdapter(provider.GetRequiredService<IMethodCacheBenchmarkService>(),
            provider.GetRequiredService<ICacheManager>()), provider);
    }

    private sealed class AdapterScope(ICacheAdapter adapter, IDisposable? owner = null) : IDisposable
    {
        public ICacheAdapter Adapter { get; } = adapter;
        public void Dispose() { Adapter.Dispose(); owner?.Dispose(); }
    }
}
