using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Core.Runtime.Defaults;

namespace MethodCache.Benchmarks.Core;

/// <summary>
/// Simplified base class for benchmarks with basic setup
/// </summary>
public abstract class SimpleBenchmarkBase
{
    protected IServiceProvider ServiceProvider { get; private set; } = null!;
    protected ICacheManager CacheManager { get; private set; } = null!;

    [GlobalSetup]
    public virtual void GlobalSetup()
    {
        var services = new ServiceCollection();
        
        // Add basic logging
        services.AddLogging(builder => builder
            .SetMinimumLevel(LogLevel.Warning)
            .AddConsole());

        // Add MethodCache configuration
        services.AddSingleton<MethodCacheConfiguration>(provider =>
        {
            var config = new MethodCacheConfiguration();
            config.DefaultDuration(TimeSpan.FromMinutes(10));
            return config;
        });

        // Add basic services
        services.AddSingleton<ICacheKeyGenerator, DefaultCacheKeyGenerator>();
        services.AddSingleton<ICacheMetricsProvider, ConsoleCacheMetricsProvider>();
        services.AddSingleton<ICacheManager, InMemoryCacheManager>();
        
        ConfigureBenchmarkServices(services);
        
        ServiceProvider = services.BuildServiceProvider();
        CacheManager = ServiceProvider.GetRequiredService<ICacheManager>();
        
        OnSetupComplete();
    }

    [GlobalCleanup]
    public virtual void GlobalCleanup()
    {
        OnCleanupStart();
        if (ServiceProvider is IDisposable disposableProvider)
            disposableProvider.Dispose();
    }

    protected virtual void ConfigureBenchmarkServices(IServiceCollection services)
    {
        // Override in derived classes for specific benchmark services
    }

    protected virtual void OnSetupComplete() { }
    protected virtual void OnCleanupStart() { }
}