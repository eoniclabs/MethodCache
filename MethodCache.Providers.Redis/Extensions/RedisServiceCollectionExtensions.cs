using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MethodCache.Core;
using MethodCache.Infrastructure.Extensions;
using MethodCache.HybridCache.Extensions;
using MethodCache.Providers.Redis.Configuration;
using MethodCache.Providers.Redis.HealthChecks;
using MethodCache.Providers.Redis.Infrastructure;

namespace MethodCache.Providers.Redis.Extensions;

/// <summary>
/// Extension methods for configuring Redis cache providers.
/// </summary>
public static class RedisServiceCollectionExtensions
{
    /// <summary>
    /// Adds Redis Infrastructure for caching with the specified options.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureRedis">Configuration for Redis options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddRedisInfrastructure(
        this IServiceCollection services,
        Action<RedisOptions>? configureRedis = null)
    {
        return services.AddRedisInfrastructure(configureRedis, null);
    }

    /// <summary>
    /// Adds Redis Infrastructure with hybrid storage manager for L1+L2 caching.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureRedis">Configuration for Redis options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddRedisHybridInfrastructure(
        this IServiceCollection services,
        Action<RedisOptions>? configureRedis = null)
    {
        // Add Redis infrastructure
        services.AddRedisInfrastructure(configureRedis);

        // Add hybrid storage manager
        services.AddHybridStorageManager();

        return services;
    }

    /// <summary>
    /// Adds Redis Infrastructure with health checks.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureRedis">Configuration for Redis options.</param>
    /// <param name="healthCheckName">Name for the health check (default: "redis_infrastructure").</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddRedisInfrastructureWithHealthChecks(
        this IServiceCollection services,
        Action<RedisOptions>? configureRedis = null,
        string healthCheckName = "redis_infrastructure")
    {
        services.AddRedisInfrastructure(configureRedis);

        // Add health checks
        services.AddHealthChecks()
            .AddCheck<HealthChecks.RedisInfrastructureHealthCheck>(healthCheckName);
        services.TryAddSingleton<HealthChecks.RedisInfrastructureHealthCheck>();

        return services;
    }

    /// <summary>
    /// Adds Redis cache services with MethodCache core integration.
    /// This is a convenience method that registers both MethodCache and Redis Infrastructure.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Optional Redis configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddRedisCache(
        this IServiceCollection services,
        Action<RedisOptions>? configureOptions = null)
    {
        // Ensure MethodCache core is registered
        services.AddMethodCache();

        // Add Redis infrastructure
        services.AddRedisInfrastructure(configureOptions);

        return services;
    }
}