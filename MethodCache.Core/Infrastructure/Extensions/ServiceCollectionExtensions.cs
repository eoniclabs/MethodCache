using MethodCache.Core.Configuration;
using MethodCache.Core.Infrastructure.Configuration;
using MethodCache.Core.Infrastructure.Serialization;
using MethodCache.Core.Infrastructure.Services;
using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.Execution;
using MethodCache.Core.Runtime.KeyGeneration;
using MethodCache.Core.Storage;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Core.Storage.Coordination;
using MethodCache.Core.Storage.Layers.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using McmMemoryCacheOptions = Microsoft.Extensions.Caching.Memory.MemoryCacheOptions;

namespace MethodCache.Core.Infrastructure.Extensions;

/// <summary>
/// Extension methods for configuring MethodCache services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds core infrastructure services to the service collection.
    /// Internal plumbing for the Redis and SQL Server providers until they are wired into method caching.
    /// </summary>
    internal static IServiceCollection AddCacheInfrastructure(
        this IServiceCollection services,
        Action<StorageOptions>? configure = null)
    {
        if (configure != null)
        {
            services.Configure(configure);
        }

        // Register core services
        services.TryAddSingleton<ISerializer, MessagePackSerializer>();
        services.Replace(ServiceDescriptor.Singleton<IMemoryStorage, MemoryStorage>());

        // Add memory cache if not already registered
        services.AddMemoryCache();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<McmMemoryCacheOptions>, StorageMemoryCacheConfigurator>());

        return services;
    }

    /// <summary>
    /// Adds hybrid storage manager as the primary storage provider.
    /// </summary>
    internal static IServiceCollection AddHybridStorageManager(
        this IServiceCollection services,
        Action<StorageOptions>? configure = null)
    {
        services.AddCacheInfrastructure(configure);

        // Register storage coordinator as the primary storage provider
        services.TryAddSingleton<StorageCoordinator>(provider =>
        {
            var memoryStorage = provider.GetRequiredService<IMemoryStorage>();
            var options = provider.GetRequiredService<IOptions<StorageOptions>>();
            var logger = provider.GetRequiredService<ILogger<StorageCoordinator>>();
            var metrics = provider.GetService<ICacheMetricsProvider>();

            // Try to get L2 and L3 storage providers and backplane (optional)
            // For L2, we can look for any registered IStorageProvider (but not this coordinator itself)
            // For L3, we look for IPersistentStorageProvider
            // To avoid circular dependency, we'll get them by looking for different service names
            var backplane = provider.GetService<IBackplane>();

            // For now, don't try to automatically wire L2/L3 to avoid circular dependencies
            // Tests can manually configure these if needed
            return StorageCoordinatorFactory.Create(memoryStorage, options, logger, null, null, backplane, metrics);
        });

        // Override any existing IStorageProvider registration with coordinator
        services.Replace(ServiceDescriptor.Singleton<IStorageProvider>(provider => provider.GetRequiredService<StorageCoordinator>()));
        return services;
    }

    /// <summary>
    /// Adds generic cache warming service that works with any storage provider
    /// </summary>
    public static IServiceCollection AddCacheWarming(this IServiceCollection services)
    {
        services.TryAddSingleton<ICacheWarmingService, CacheWarmingService>();
        services.TryAddSingleton<CacheWarmingService>();
        services.AddHostedService<CacheWarmingService>();

        return services;
    }
}
