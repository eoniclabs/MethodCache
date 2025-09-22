using System;
using MethodCache.Core;
using MethodCache.HybridCache.Abstractions;
using MethodCache.HybridCache.Configuration;
using MethodCache.HybridCache.Implementation;
using MethodCache.Infrastructure.Abstractions;
using MethodCache.Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MethodCache.HybridCache.Extensions;

/// <summary>
/// Extension methods for configuring hybrid cache services with Infrastructure layer support.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Adds hybrid cache services using the Infrastructure layer with memory-only storage.
    /// This provides L1 (memory) caching through the Infrastructure layer.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Optional hybrid cache configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddInfrastructureHybridCache(
        this IServiceCollection services,
        Action<HybridCacheOptions>? configureOptions = null)
    {
        // Configure hybrid cache options
        services.Configure<HybridCacheOptions>(options =>
        {
            options.L2Enabled = false;
            options.Strategy = HybridStrategy.L1Only;
            configureOptions?.Invoke(options);
        });

        // Add memory-only infrastructure (provides IMemoryStorage, but no L2 storage)
        services.AddMemoryOnlyStorage();

        // Register hybrid cache manager using Infrastructure
        services.TryAddSingleton<IHybridCacheManager>(provider =>
        {
            var storageProvider = provider.GetRequiredService<IStorageProvider>();
            var memoryStorage = provider.GetRequiredService<IMemoryStorage>();
            var backplane = provider.GetService<IBackplane>();
            var options = provider.GetRequiredService<IOptions<HybridCacheOptions>>();
            var logger = provider.GetRequiredService<ILogger<HybridCacheManager>>();

            return new HybridCacheManager(storageProvider, memoryStorage, backplane, options, logger);
        });

        // Register as primary cache manager for backwards compatibility
        services.TryAddSingleton<ICacheManager>(provider =>
            provider.GetRequiredService<IHybridCacheManager>());

        return services;
    }

    /// <summary>
    /// Adds hybrid cache services using the Infrastructure layer with full L1+L2 support.
    /// Requires that an IStorageProvider has been registered (e.g., via AddRedisInfrastructure).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Optional hybrid cache configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddInfrastructureHybridCacheWithL2(
        this IServiceCollection services,
        Action<HybridCacheOptions>? configureOptions = null)
    {
        // Configure hybrid cache options
        services.Configure<HybridCacheOptions>(options =>
        {
            options.L2Enabled = true;
            options.Strategy = HybridStrategy.WriteThrough;
            configureOptions?.Invoke(options);
        });

        // Add hybrid storage manager (provides L1+L2 coordination)
        services.AddHybridStorageManager();

        // Register hybrid cache manager using Infrastructure
        services.TryAddSingleton<IHybridCacheManager>(provider =>
        {
            var storageProvider = provider.GetRequiredService<IStorageProvider>();
            var memoryStorage = provider.GetRequiredService<IMemoryStorage>();
            var backplane = provider.GetService<IBackplane>();
            var options = provider.GetRequiredService<IOptions<HybridCacheOptions>>();
            var logger = provider.GetRequiredService<ILogger<HybridCacheManager>>();

            return new HybridCacheManager(storageProvider, memoryStorage, backplane, options, logger);
        });

        // Register as primary cache manager for backwards compatibility
        services.TryAddSingleton<ICacheManager>(provider =>
            provider.GetRequiredService<IHybridCacheManager>());

        return services;
    }

    /// <summary>
    /// Adds hybrid cache services using the advanced Memory provider with sophisticated features.
    /// This provides L1 caching with advanced eviction policies, memory tracking, and tag support.
    /// Requires MethodCache.Providers.Memory package.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Optional hybrid cache configuration.</param>
    /// <param name="configureMemory">Optional memory provider configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAdvancedMemoryHybridCache(
        this IServiceCollection services,
        Action<HybridCacheOptions>? configureOptions = null,
        Action<object>? configureMemory = null)
    {
        // Configure hybrid cache options
        services.Configure<HybridCacheOptions>(options =>
        {
            options.L2Enabled = false;
            options.Strategy = HybridStrategy.L1Only;
            configureOptions?.Invoke(options);
        });

        // Use reflection to add advanced memory infrastructure if available
        try
        {
            var memoryProviderType = Type.GetType("MethodCache.Providers.Memory.Extensions.MemoryServiceCollectionExtensions, MethodCache.Providers.Memory");
            if (memoryProviderType != null)
            {
                var addAdvancedMemoryMethod = memoryProviderType.GetMethod("AddAdvancedMemoryInfrastructure");
                if (addAdvancedMemoryMethod != null)
                {
                    addAdvancedMemoryMethod.Invoke(null, new object?[] { services, configureMemory });
                }
                else
                {
                    throw new InvalidOperationException("MethodCache.Providers.Memory is available but AddAdvancedMemoryInfrastructure method not found");
                }
            }
            else
            {
                throw new InvalidOperationException("MethodCache.Providers.Memory package is required for AddAdvancedMemoryHybridCache");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to configure advanced memory provider. Ensure MethodCache.Providers.Memory package is installed and configured properly.",
                ex);
        }

        // Register hybrid cache manager using Infrastructure
        services.TryAddSingleton<IHybridCacheManager>(provider =>
        {
            var storageProvider = provider.GetRequiredService<IStorageProvider>();
            var memoryStorage = provider.GetRequiredService<IMemoryStorage>();
            var backplane = provider.GetService<IBackplane>();
            var options = provider.GetRequiredService<IOptions<HybridCacheOptions>>();
            var logger = provider.GetRequiredService<ILogger<HybridCacheManager>>();

            return new HybridCacheManager(storageProvider, memoryStorage, backplane, options, logger);
        });

        // Register as primary cache manager for backwards compatibility
        services.TryAddSingleton<ICacheManager>(provider =>
            provider.GetRequiredService<IHybridCacheManager>());

        return services;
    }


}

