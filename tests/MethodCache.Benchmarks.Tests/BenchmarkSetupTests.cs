using MethodCache.Benchmarks.Core;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Benchmarks.Tests;

public class BenchmarkSetupTests
{
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
