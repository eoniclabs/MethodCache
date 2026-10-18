using MethodCache.Benchmarks.Core;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Benchmarks.Tests;

public class BenchmarkSetupTests
{
    [Fact]
    public void GeneratedContract_AllHitShapesStayResident_AndEveryMissLoads()
    {
        var benchmark = new MethodCache.Benchmarks.Comparison.GeneratedContractBenchmarks { SelectPerformanceCores = false };
        benchmark.Setup();
        try
        {
            Assert.Equal(8, benchmark.OriginLoads);
            benchmark.VerifyResidentHits();
            Assert.Equal(8, benchmark.OriginLoads);
            benchmark.VerifyUniqueMisses();
            Assert.Equal(38, benchmark.OriginLoads);
        }
        finally { benchmark.Cleanup(); }
    }

    [Fact]
    public void GeneratedContract_ResidentGuardRejectsUncachedService()
    {
        var benchmark = new MethodCache.Benchmarks.Comparison.GeneratedContractBenchmarks { SelectPerformanceCores = false };
        benchmark.Setup();
        try
        {
            typeof(MethodCache.Benchmarks.Comparison.GeneratedContractBenchmarks)
                .GetField("_service", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(benchmark, benchmark.OriginService);
            Assert.Throws<InvalidOperationException>(() => benchmark.VerifyResidentHits());
        }
        finally { benchmark.Cleanup(); }
    }

    [Fact]
    public void ComponentPaths_MatchReferencedCoreCapabilities()
    {
        var core = typeof(MethodCache.Generated.CachedMethod).Assembly;
        Assert.Equal(core.GetType("MethodCache.Core.Runtime.Keys.PrehashedKey") != null,
            MethodCache.Benchmarks.Microbenchmarks.KeyHashLookupBenchmarks.UsesPrefixHash);
        Assert.Equal(core.GetType("MethodCache.Core.Runtime.Keys.SipHashState") != null,
            MethodCache.Benchmarks.Microbenchmarks.KeyHashLookupBenchmarks.UsesSipHash);
        Assert.Equal(core.GetType("MethodCache.Core.Runtime.Keys.KeyPrefixHash") != null,
            MethodCache.Benchmarks.Microbenchmarks.KeyHashLookupBenchmarks.UsesAsciiHash);
        var hasInPlaceBuilder = typeof(MethodCache.Generated.KeyBuilder).GetConstructors().Any(constructor =>
        {
            var parameters = constructor.GetParameters();
            return parameters.Length == 2 && parameters[0].ParameterType == typeof(MethodCache.Generated.MethodCall) &&
                parameters[1].ParameterType == typeof(Span<char>);
        });
        Assert.Equal(hasInPlaceBuilder,
            MethodCache.Benchmarks.Microbenchmarks.PrefixHitPathBenchmarks.UsesInPlaceKeyBuilder);
    }

    [Theory]
    [InlineData("engine")]
    [InlineData("hitpath")]
    [InlineData("generated-hitpath")]
    [InlineData("httppath")]
    [InlineData("httpcold")]
    [InlineData("hashlookup")]
    [InlineData("memory-reference")]
    [InlineData("contract")]
    [InlineData("readme")]
    [InlineData("comparison")]
    public void BenchmarkCommand_List_ReturnsWithoutRunningBenchmarks(string category)
    {
        var error = Record.Exception(() => MethodCache.Benchmarks.BenchmarkCommands.Run(new[] { category, "--list", "flat" }));
        Assert.Null(error);
    }

    [Theory]
    [InlineData(4, "Int")]
    [InlineData(80, "Int")]
    [InlineData(256, "Int")]
    [InlineData(4, "Guid")]
    [InlineData(80, "Guid")]
    [InlineData(256, "Guid")]
    public void KeyHashLookup_AllPathsReadThePopulatedValue(int length, string argument)
    {
        var benchmark = new MethodCache.Benchmarks.Microbenchmarks.KeyHashLookupBenchmarks { PrefixCharacters = length, ArgumentKind = argument, SelectPerformanceCores = false };
        benchmark.Setup();
        Assert.Equal(42, benchmark.ReadyStringLookup());
        Assert.Equal(42, benchmark.FullSpanLookup());
        Assert.Equal(42, benchmark.PrefixLookup());
    }

    [Theory]
    [InlineData(4, 128, false)]
    [InlineData(4, 128, true)]
    [InlineData(4, 512, false)]
    [InlineData(4, 512, true)]
    [InlineData(256, 128, false)]
    [InlineData(256, 128, true)]
    [InlineData(256, 512, false)]
    [InlineData(256, 512, true)]
    public async Task PrefixHitPath_SetupPopulatesAllShapes_AndMeasuredHitsNeverRunOrigins(int prefix, int buffer, bool listening)
    {
        var benchmark = new MethodCache.Benchmarks.Microbenchmarks.PrefixHitPathBenchmarks
        { MethodNameLength = prefix, BufferCharacters = buffer, Listening = listening, SelectPerformanceCores = false };
        benchmark.Setup();
        try
        {
            Assert.Equal(1, benchmark.OriginLoads);
            for (var i = 0; i < 100; i++)
            {
                Assert.Equal(42, benchmark.SyncHit());
                Assert.Equal(42, await benchmark.TaskHit());
                Assert.Equal(42, await benchmark.ValueTaskHit());
                Assert.True(benchmark.KeyBuild() > 0);
            }
            Assert.Equal(1, benchmark.OriginLoads);
        }
        finally { benchmark.Cleanup(); }
    }

    [Fact]
    public async Task MemoryReference_AllFourPathsReturnTheResidentValue()
    {
        var benchmark = new MethodCache.Benchmarks.Microbenchmarks.MemoryCacheLookupReferenceBenchmarks { SelectPerformanceCores = false };
        benchmark.Setup();
        try
        {
            Assert.Equal(42, benchmark.ReadyKeySync());
            Assert.Equal(42, benchmark.BuiltKeySync());
            Assert.Equal(42, await benchmark.ReadyKeyTask());
            Assert.Equal(42, await benchmark.BuiltKeyTask());
        }
        finally { benchmark.Cleanup(); }
    }

    [Fact]
    public async Task ReadmeComparison_HitsReturnOneUserPerKeyType_AndEveryMissUsesANewId()
    {
        var benchmark = new MethodCache.Benchmarks.Comparison.ReadmeComparisonBenchmarks();
        await benchmark.Setup();
        try
        {
            var hit = (await benchmark.MethodCacheHit()).Id;
            Assert.Equal(hit, (await benchmark.MemoryCacheHit()).Id);
            Assert.Equal(hit, (await benchmark.HybridCacheHit()).Id);
            Assert.Equal(hit, (await benchmark.FusionCacheHit()).Id);

            var guidHit = (await benchmark.MethodCacheGuidHit()).Id;
            Assert.NotEqual(Guid.Empty, guidHit);
            Assert.Equal(guidHit, (await benchmark.MemoryCacheGuidHit()).Id);
            Assert.Equal(guidHit, (await benchmark.HybridCacheGuidHit()).Id);
            Assert.Equal(guidHit, (await benchmark.FusionCacheGuidHit()).Id);

            var misses = new[]
            {
                (await benchmark.MemoryCacheMiss()).Id, (await benchmark.MethodCacheMiss()).Id,
                (await benchmark.HybridCacheMiss()).Id, (await benchmark.FusionCacheMiss()).Id,
            };
            Assert.Equal(misses.Length, misses.Distinct().Count());
            Assert.DoesNotContain(hit, misses);

            var guidMisses = new[]
            {
                (await benchmark.MemoryCacheGuidMiss()).Id, (await benchmark.MethodCacheGuidMiss()).Id,
                (await benchmark.HybridCacheGuidMiss()).Id, (await benchmark.FusionCacheGuidMiss()).Id,
            };
            Assert.Equal(guidMisses.Length, guidMisses.Distinct().Count());
            Assert.DoesNotContain(guidHit, guidMisses);
        }
        finally { await benchmark.Cleanup(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpPath_ActualHitsNeverCallOrigin_And304AndMissDo(bool listening)
    {
        var benchmark = new MethodCache.Benchmarks.Microbenchmarks.HttpResponsePathBenchmarks { Listening = listening };
        await benchmark.Setup();
        try
        {
            var loads = benchmark.OriginLoads;
            for (var i = 0; i < 20; i++) { Assert.Equal(200, await benchmark.FreshHit()); Assert.Equal(200, await benchmark.VariantHit()); }
            Assert.Equal(loads, benchmark.OriginLoads);
            Assert.Equal(200, await benchmark.Revalidated()); Assert.Equal(loads + 1, benchmark.OriginLoads);
            Assert.Equal(200, await benchmark.Miss()); Assert.Equal(loads + 2, benchmark.OriginLoads);
        }
        finally { benchmark.Cleanup(); }
    }

    [Fact]
    public void BenchmarkBase_ActualRegistrationFactories_HaveNoCacheManagerCycle()
    {
        var benchmark = new ProbeBenchmark();
        var services = benchmark.Registrations();
        using var fallback = services.BuildServiceProvider();
        var graph = new FactoryGraph(services, fallback);
        object? manager = null;
        var error = Record.Exception(() => manager = graph.GetService(typeof(ICacheManager)));
        try { Assert.Null(error); }
        finally { (manager as IDisposable)?.Dispose(); }
    }

    [Fact]
    public async Task BenchmarkBase_Setup_CacheCanPopulateAndHit()
    {
        var benchmark = new ProbeBenchmark();
        benchmark.GlobalSetup();
        try { await AssertUsableAsync(benchmark.Manager); }
        finally { benchmark.GlobalCleanup(); }
    }

    [Fact]
    public async Task SimpleBenchmarkBase_Setup_CacheCanPopulateAndHit()
    {
        var benchmark = new ProbeSimpleBenchmark();
        benchmark.GlobalSetup();
        try { await AssertUsableAsync(benchmark.Manager); }
        finally { benchmark.GlobalCleanup(); }
    }

    private static async Task AssertUsableAsync(ICacheManager manager)
    {
        var calls = 0;
        Task<int> Factory() { calls++; return Task.FromResult(42); }
        var policy = CacheRuntimePolicy.Empty("setup") with { Duration = TimeSpan.FromMinutes(1) };
        Assert.Equal(42, await manager.GetOrCreateFastAsync("setup", "setup", Factory, policy));
        Assert.Equal(42, await manager.GetOrCreateFastAsync("setup", "setup", Factory, policy));
        Assert.Equal(1, calls);
    }

    private sealed class ProbeSimpleBenchmark : SimpleBenchmarkBase
    {
        public ICacheManager Manager => CacheManager;
    }

    private sealed class ProbeBenchmark : BenchmarkBase
    {
        public ICacheManager Manager => ServiceProvider.GetRequiredService<ICacheManager>();
        public IServiceCollection Registrations()
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            return services;
        }
    }

    /// <summary>Executes the actual factories, reporting a cycle before the real container would wait forever.</summary>
    private sealed class FactoryGraph(IServiceCollection services, IServiceProvider fallback) : IServiceProvider
    {
        private readonly List<Type> _path = [];
        public object? GetService(Type type)
        {
            if (_path.Contains(type)) throw new InvalidOperationException(string.Join(" -> ", _path.Append(type).Select(t => t.Name)));
            _path.Add(type);
            try
            {
                var descriptor = services.LastOrDefault(d => !d.IsKeyedService && d.ServiceType == type);
                return (type == typeof(ICacheManager) || type == typeof(InMemoryCacheManager)) && descriptor?.ImplementationFactory is { } factory
                    ? factory(this)
                    : fallback.GetService(type);
            }
            finally { _path.RemoveAt(_path.Count - 1); }
        }
    }
}
