using MethodCache.Benchmarks.Core;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Benchmarks.Tests;

public class BenchmarkSetupTests
{
    [Theory]
    [InlineData("engine")]
    [InlineData("hitpath")]
    [InlineData("httppath")]
    [InlineData("httpcold")]
    [InlineData("hashlookup")]
    [InlineData("memory-reference")]
    [InlineData("readme")]
    [InlineData("comparison")]
    public void BenchmarkCommand_List_ReturnsWithoutRunningBenchmarks(string category)
    {
        var error = Record.Exception(() => MethodCache.Benchmarks.BenchmarkCommands.Run(new[] { category, "--list", "flat" }));
        Assert.Null(error);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(80)]
    [InlineData(256)]
    public void KeyHashLookup_AllPathsReadThePopulatedValue(int length)
    {
        var benchmark = new MethodCache.Benchmarks.Microbenchmarks.KeyHashLookupBenchmarks { PrefixCharacters = length, SelectPerformanceCores = false };
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
