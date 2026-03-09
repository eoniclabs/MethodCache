using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MethodCache.Core;
using MethodCache.Core.Infrastructure.Extensions;
using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.Execution;

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

        ConfigureBenchmarkServices(services);

        services.AddMethodCache(config =>
        {
            config.DefaultPolicy(builder => builder.WithDuration(TimeSpan.FromMinutes(10)));
        }, typeof(SimpleBenchmarkBase).Assembly);

        services.AddSingleton(sp => (InMemoryCacheManager)sp.GetRequiredService<ICacheManager>());
        
        ServiceProvider = services.BuildServiceProvider();
        CacheManager = ServiceProvider.GetRequiredService<ICacheManager>();
        
        OnSetupComplete();
    }

    [GlobalCleanup]
    public virtual void GlobalCleanup()
    {
        OnCleanupStart();
        try
        {
            if (ServiceProvider is IAsyncDisposable asyncDisposableProvider)
            {
                asyncDisposableProvider.DisposeAsync().AsTask().GetAwaiter().GetResult();
                return;
            }

            if (ServiceProvider is IDisposable disposableProvider)
            {
                disposableProvider.Dispose();
            }
        }
        catch (ObjectDisposedException)
        {
            // Benchmark teardown can race with already-disposed internals in transient runners.
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("CancellationTokenSource has been disposed", StringComparison.Ordinal))
        {
            // Ignore duplicate disposal failures during benchmark process shutdown.
        }
    }

    protected virtual void ConfigureBenchmarkServices(IServiceCollection services)
    {
        // Override in derived classes for specific benchmark services
    }

    protected virtual void OnSetupComplete() { }
    protected virtual void OnCleanupStart() { }
}
