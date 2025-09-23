using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Core.Runtime.Defaults;


using MethodCache.Providers.Redis;
using System.Linq;
using MethodCache.Core.Storage;
using MethodCache.Providers.Redis.Configuration;

namespace MethodCache.Benchmarks.Core;

/// <summary>
/// Base class for all benchmarks providing common setup and configuration
/// </summary>
public abstract class BenchmarkBase
{
    protected IServiceProvider ServiceProvider { get; private set; } = null!;
    protected ILogger Logger { get; private set; } = null!;

    [GlobalSetup]
    public virtual void GlobalSetup()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider = services.BuildServiceProvider();
        Logger = ServiceProvider.GetRequiredService<ILogger<BenchmarkBase>>();
        
        OnSetupComplete();
    }

    [GlobalCleanup]
    public virtual void GlobalCleanup()
    {
        OnCleanupStart();
        if (ServiceProvider is IDisposable disposableProvider)
            disposableProvider.Dispose();
    }

    protected virtual void ConfigureServices(IServiceCollection services)
    {
        // Add logging
        services.AddLogging(builder => builder
            .SetMinimumLevel(LogLevel.Warning)
            .AddConsole());

        // Add MethodCache configuration
        services.AddSingleton<MethodCache.Core.Configuration.MethodCacheConfiguration>(provider =>
        {
            var config = new MethodCache.Core.Configuration.MethodCacheConfiguration();
            config.DefaultDuration(TimeSpan.FromMinutes(10));
            return config;
        });

        // Add key generator
        services.AddSingleton<ICacheKeyGenerator, DefaultCacheKeyGenerator>();

        // Add default cache metrics provider
        services.AddSingleton<ICacheMetricsProvider, ConsoleCacheMetricsProvider>();

        // Configure specific cache providers
        ConfigureCacheProviders(services);
        
        // Configure benchmark-specific services
        ConfigureBenchmarkServices(services);
    }

    protected virtual void ConfigureCacheProviders(IServiceCollection services)
    {
        // In-Memory Cache
        services.AddSingleton<InMemoryCacheManager>();
        
        // Use InMemoryCacheManager for benchmarks instead of hybrid
        services.AddSingleton<ICacheManager, InMemoryCacheManager>();
        services.Configure<MethodCache.Core.Storage.HybridCacheOptions>(options =>
        {
            options.L1MaxItems = 1000;
            options.L2DefaultExpiration = TimeSpan.FromMinutes(30);
            options.L2Enabled = true;
        });

        // Redis Cache (if available)
        try
        {
            // Skip Redis setup in base - too complex for simple benchmark base
            // Individual benchmarks can set up Redis if needed
        }
        catch
        {
            // Redis not available, skip
        }
    }

    protected virtual void ConfigureBenchmarkServices(IServiceCollection services)
    {
        // Override in derived classes for specific benchmark services
    }

    protected virtual void OnSetupComplete() { }
    protected virtual void OnCleanupStart() { }

    /// <summary>
    /// Creates a service provider with specific cache manager
    /// </summary>
    protected IServiceProvider CreateServiceProviderWithCache<TCacheManager>() 
        where TCacheManager : class, ICacheManager
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        
        // Remove all existing ICacheManager registrations safely
        var descriptors = services
            .Where(d => d.ServiceType == typeof(ICacheManager))
            .ToList();

        foreach (var descriptor in descriptors)
        {
            services.Remove(descriptor);
        }

        // Add the specific cache manager for this benchmark
        services.AddSingleton<ICacheManager, TCacheManager>();
        
        return services.BuildServiceProvider();
    }
}