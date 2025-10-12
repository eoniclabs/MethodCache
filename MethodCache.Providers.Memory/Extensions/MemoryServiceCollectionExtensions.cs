using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MethodCache.Core.Storage;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Providers.Memory.Configuration;
using MethodCache.Providers.Memory.Infrastructure;

namespace MethodCache.Providers.Memory.Extensions;

public static class MemoryServiceCollectionExtensions
{
    public static IServiceCollection AddAdvancedMemoryStorage(
        this IServiceCollection services,
        Action<AdvancedMemoryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure != null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<IMemoryStorage, AdvancedMemoryStorage>();
        services.TryAddSingleton<IStorageProvider, AdvancedMemoryStorageProvider>();

        return services;
    }

    public static IServiceCollection AddAdvancedMemoryCache(
        this IServiceCollection services,
        Action<AdvancedMemoryOptions>? configure = null)
    {
        return services.AddAdvancedMemoryStorage(configure);
    }

    /// <summary>
    /// Adds advanced memory storage as the infrastructure layer for hybrid caching.
    /// This integrates the sophisticated Memory provider with the Infrastructure layer.
    /// </summary>
    public static IServiceCollection AddAdvancedMemoryInfrastructure(
        this IServiceCollection services,
        Action<AdvancedMemoryOptions>? configureMemory = null)
    {
        // Add advanced memory storage services
        services.AddAdvancedMemoryStorage(configureMemory);

        // Also add core infrastructure services
        services.TryAddSingleton<ISerializer,
            MethodCache.Core.Infrastructure.Serialization.MessagePackSerializer>();

        return services;
    }
}