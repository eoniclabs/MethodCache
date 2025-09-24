using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core.Storage;
using MethodCache.Core.Configuration;
using MethodCache.Infrastructure.Implementation;
using MethodCache.Infrastructure.Services;

namespace MethodCache.Infrastructure.Extensions;

/// <summary>
/// Extension methods for registering infrastructure services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds core infrastructure services to the service collection.
    /// </summary>
    public static IServiceCollection AddCacheInfrastructure(
        this IServiceCollection services,
        Action<StorageOptions>? configure = null)
    {
        if (configure != null)
        {
            services.Configure(configure);
        }

        // Register core services
        services.TryAddSingleton<ISerializer, MessagePackSerializer>();
        services.TryAddSingleton<IMemoryStorage, Implementation.MemoryStorage>();

        // Add memory cache if not already registered
        services.AddMemoryCache();

        return services;
    }

    /// <summary>
    /// Adds hybrid storage manager as the primary storage provider.
    /// </summary>
    public static IServiceCollection AddHybridStorageManager(
        this IServiceCollection services,
        Action<StorageOptions>? configure = null)
    {
        services.AddCacheInfrastructure(configure);

        // Register hybrid storage manager as the primary storage provider
        services.TryAddScoped<Implementation.HybridStorageManager>(provider =>
        {
            var memoryStorage = provider.GetRequiredService<IMemoryStorage>();
            var options = provider.GetRequiredService<IOptions<StorageOptions>>();
            var logger = provider.GetRequiredService<ILogger<Implementation.HybridStorageManager>>();

            // Try to get L2 and L3 storage providers and backplane (optional)
            // For L2, we can look for any registered IStorageProvider (but not this hybrid manager itself)
            // For L3, we look for IPersistentStorageProvider
            // To avoid circular dependency, we'll get them by looking for different service names
            var backplane = provider.GetService<IBackplane>();

            // For now, don't try to automatically wire L2/L3 to avoid circular dependencies
            // Tests can manually configure these if needed
            return new Implementation.HybridStorageManager(memoryStorage, options, logger, null, null, backplane);
        });

        // Override any existing IStorageProvider registration with hybrid manager
        services.AddScoped<IStorageProvider>(provider => provider.GetRequiredService<Implementation.HybridStorageManager>());
        return services;
    }

    /// <summary>
    /// Adds memory-only storage (L1 only).
    /// </summary>
    public static IServiceCollection AddMemoryOnlyStorage(
        this IServiceCollection services,
        Action<StorageOptions>? configure = null)
    {
        services.AddCacheInfrastructure(configure);

        // Register memory storage as the primary storage provider
        services.TryAddScoped<IStorageProvider>(provider =>
        {
            var memoryStorage = provider.GetRequiredService<IMemoryStorage>();
            return new MemoryOnlyStorageProvider(memoryStorage);
        });

        return services;
    }

    /// <summary>
    /// Adds advanced memory storage using the Memory provider with sophisticated features.
    /// This is now the recommended default for memory-only scenarios.
    /// </summary>
    public static IServiceCollection AddAdvancedMemoryOnlyStorage(
        this IServiceCollection services,
        Action<StorageOptions>? configureStorage = null)
    {
        // Register infrastructure serializer
        services.TryAddSingleton<ISerializer, MessagePackSerializer>();

        // Configure storage options if provided
        if (configureStorage != null)
        {
            services.Configure(configureStorage);
        }

        // NOTE: The advanced memory storage and provider are registered by calling:
        // services.AddAdvancedMemoryStorage() from MethodCache.Providers.Memory
        // This method assumes that has already been called.

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

    /// <summary>
    /// Validates that all required infrastructure services are registered.
    /// </summary>
    public static IServiceCollection ValidateInfrastructure(this IServiceCollection services)
    {
        // This will be called during DI container validation
        services.AddSingleton<InfrastructureValidator>();
        return services;
    }
}

/// <summary>
/// Adapter to make IMemoryStorage work as IStorageProvider for memory-only scenarios.
/// </summary>
internal class MemoryOnlyStorageProvider : IStorageProvider
{
    private readonly IMemoryStorage _memoryStorage;

    public MemoryOnlyStorageProvider(IMemoryStorage memoryStorage)
    {
        _memoryStorage = memoryStorage;
    }

    public string Name => "Memory-Only";

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        return _memoryStorage.GetAsync<T>(key, cancellationToken);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        return _memoryStorage.SetAsync(key, value, expiration, cancellationToken);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        return _memoryStorage.SetAsync(key, value, expiration, tags, cancellationToken);
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        return _memoryStorage.RemoveAsync(key, cancellationToken);
    }

    public Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        return _memoryStorage.RemoveByTagAsync(tag, cancellationToken);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_memoryStorage.Exists(key));
    }

    public Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy);
    }

    public Task<StorageStats?> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var memStats = _memoryStorage.GetStats();
        var stats = new StorageStats
        {
            GetOperations = memStats.Hits + memStats.Misses,
            SetOperations = memStats.EntryCount, // Approximate
            RemoveOperations = 0, // Not tracked
            AverageResponseTimeMs = 0.1, // Memory is fast
            ErrorCount = 0,
            AdditionalStats = new Dictionary<string, object>
            {
                ["Hits"] = memStats.Hits,
                ["Misses"] = memStats.Misses,
                ["HitRatio"] = memStats.HitRatio,
                ["EntryCount"] = memStats.EntryCount,
                ["Evictions"] = memStats.Evictions,
                ["TagMappingCount"] = memStats.TagMappingCount,
                ["EstimatedMemoryUsage"] = memStats.EstimatedMemoryUsage
            }
        };

        return Task.FromResult<StorageStats?>(stats);
    }
}

/// <summary>
/// Validates that infrastructure is properly configured.
/// </summary>
internal class InfrastructureValidator
{
    public InfrastructureValidator(
        IStorageProvider storageProvider,
        IMemoryStorage memoryStorage,
        ISerializer serializer)
    {
        // Constructor injection validates that all required services are registered
        _ = storageProvider ?? throw new InvalidOperationException("IStorageProvider is not registered");
        _ = memoryStorage ?? throw new InvalidOperationException("IMemoryStorage is not registered");
        _ = serializer ?? throw new InvalidOperationException("ISerializer is not registered");
    }
}