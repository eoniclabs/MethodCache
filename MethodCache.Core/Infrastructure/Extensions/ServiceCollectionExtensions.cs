using MethodCache.Core.Configuration;
using MethodCache.Core.Infrastructure.Configuration;
using MethodCache.Core.Infrastructure.Serialization;
using MethodCache.Core.Infrastructure.Services;
using MethodCache.Core.Runtime.Defaults;
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
    /// Creates a MethodCache builder preconfigured with the default memory L1 provider.
    /// This is the recommended starting point for most applications.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>A builder for configuring additional cache layers.</returns>
    public static IMethodCacheBuilder AddMethodCacheBuilder(this IServiceCollection services)
    {
        if (services == null) throw new ArgumentNullException(nameof(services));

        // Register core MethodCache services
        RegisterCoreServices(services);

        // Create builder with default L1 memory provider
        var builder = new MethodCacheBuilder(services);

        // Add default memory L1 provider
        return builder.WithL1(Memory.Default());
    }

    /// <summary>
    /// Creates a MethodCache builder without registering any providers.
    /// Use this when you want full control over provider configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>A builder for configuring cache layers.</returns>
    public static IMethodCacheBuilder AddMethodCacheBuilderCore(this IServiceCollection services)
    {
        if (services == null) throw new ArgumentNullException(nameof(services));

        // Register core MethodCache services
        RegisterCoreServices(services);

        return new MethodCacheBuilder(services);
    }

    private static void RegisterCoreServices(IServiceCollection services)
    {
        // Register core cache manager
        services.TryAddSingleton<ICacheManager, InMemoryCacheManager>();

        // Register default storage options
        services.TryAddSingleton<StorageOptions>();

        // Register default memory storage implementation
        services.TryAddSingleton<IMemoryStorage, MemoryStorage>();

        // Register default cache key generator
        services.TryAddSingleton<ICacheKeyGenerator, DefaultCacheKeyGenerator>();
    }

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
        services.Replace(ServiceDescriptor.Singleton<IMemoryStorage, MemoryStorage>());

        // Add memory cache if not already registered
        services.AddMemoryCache();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<McmMemoryCacheOptions>, StorageMemoryCacheConfigurator>());

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
    /// Adds memory-only storage (L1 only).
    /// </summary>
    public static IServiceCollection AddMemoryOnlyStorage(
        this IServiceCollection services,
        Action<StorageOptions>? configure = null)
    {
        services.AddCacheInfrastructure(configure);

        // Register memory storage as the primary storage provider
        services.Replace(ServiceDescriptor.Singleton<IStorageProvider>(provider =>
        {
            var memoryStorage = provider.GetRequiredService<IMemoryStorage>();
            return new MemoryOnlyStorageProvider(memoryStorage);
        }));

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
/// Extension methods for conditional cache configuration.
/// </summary>
public static class ConditionalExtensions
{
    /// <summary>
    /// Conditionally adds an L2 provider based on a condition.
    /// </summary>
    /// <param name="builder">The cache builder.</param>
    /// <param name="condition">The condition to evaluate.</param>
    /// <param name="providerFactory">Factory function to create the provider.</param>
    /// <returns>The builder for chaining.</returns>
    public static IMethodCacheBuilder WithL2If(this IMethodCacheBuilder builder,
        bool condition, Func<IL2Provider> providerFactory)
    {
        if (builder == null) throw new ArgumentNullException(nameof(builder));
        if (providerFactory == null) throw new ArgumentNullException(nameof(providerFactory));

        return condition ? builder.WithL2(providerFactory()) : builder;
    }

    /// <summary>
    /// Conditionally adds an L3 provider based on a condition.
    /// </summary>
    /// <param name="builder">The cache builder.</param>
    /// <param name="condition">The condition to evaluate.</param>
    /// <param name="providerFactory">Factory function to create the provider.</param>
    /// <returns>The builder for chaining.</returns>
    public static IMethodCacheBuilder WithL3If(this IMethodCacheBuilder builder,
        bool condition, Func<IL3Provider> providerFactory)
    {
        if (builder == null) throw new ArgumentNullException(nameof(builder));
        if (providerFactory == null) throw new ArgumentNullException(nameof(providerFactory));

        return condition ? builder.WithL3(providerFactory()) : builder;
    }

    /// <summary>
    /// Conditionally configures the cache based on an environment variable.
    /// </summary>
    /// <param name="builder">The cache builder.</param>
    /// <param name="environmentVariable">The environment variable name.</param>
    /// <param name="expectedValue">The expected value for the condition to be true.</param>
    /// <param name="configureAction">Action to configure the cache when condition is met.</param>
    /// <returns>The builder for chaining.</returns>
    public static IMethodCacheBuilder WithEnvironmentCondition(this IMethodCacheBuilder builder,
        string environmentVariable, string expectedValue, Action<IMethodCacheBuilder> configureAction)
    {
        if (builder == null) throw new ArgumentNullException(nameof(builder));
        if (string.IsNullOrEmpty(environmentVariable)) throw new ArgumentException("Environment variable name cannot be null or empty.", nameof(environmentVariable));
        if (configureAction == null) throw new ArgumentNullException(nameof(configureAction));

        var actualValue = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.Equals(actualValue, expectedValue, StringComparison.OrdinalIgnoreCase))
        {
            configureAction(builder);
        }

        return builder;
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

    public ValueTask<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        return _memoryStorage.GetAsync<T>(key, cancellationToken);
    }

    public ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        return _memoryStorage.SetAsync(key, value, expiration, cancellationToken);
    }

    public ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        return _memoryStorage.SetAsync(key, value, expiration, tags, cancellationToken);
    }

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        return _memoryStorage.RemoveAsync(key, cancellationToken);
    }

    public ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        return _memoryStorage.RemoveByTagAsync(tag, cancellationToken);
    }

    public ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(_memoryStorage.Exists(key));
    }

    public ValueTask<Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy);
    }

    public ValueTask<StorageStats?> GetStatsAsync(CancellationToken cancellationToken = default)
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

        return ValueTask.FromResult<StorageStats?>(stats);
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
