using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MethodCache.Core;
using MethodCache.Core.Runtime.Defaults;
using MethodCache.Abstractions.Registry;
using System.Linq;

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

        services.AddMethodCache(config =>
        {
            config.DefaultPolicy(builder => builder.WithDuration(TimeSpan.FromMinutes(10)));
        }, typeof(BenchmarkBase).Assembly);

        services.AddSingleton(sp => (InMemoryCacheManager)sp.GetRequiredService<ICacheManager>());

        // Configure specific cache providers
        ConfigureCacheProviders(services);
        
        // Configure benchmark-specific services
        ConfigureBenchmarkServices(services);
    }

    protected virtual void ConfigureCacheProviders(IServiceCollection services)
    {
        // Derived benchmarks can override to replace the default cache provider
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
