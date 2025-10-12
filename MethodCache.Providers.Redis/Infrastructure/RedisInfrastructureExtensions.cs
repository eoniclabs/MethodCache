using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MethodCache.Core.Storage;
using MethodCache.Core.Configuration;
using MethodCache.Core.Infrastructure.Extensions;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Providers.Redis.Configuration;
using MethodCache.Providers.Redis.Features;
using MethodCache.Providers.Redis.Compression;
using MethodCache.Providers.Redis.Services;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace MethodCache.Providers.Redis.Infrastructure;

/// <summary>
/// Extension methods for registering Redis infrastructure services.
/// </summary>
public static class RedisInfrastructureExtensions
{
    /// <summary>
    /// Adds Redis as the distributed storage provider for the Infrastructure layer.
    /// This enables both HybridCache and HttpCache to use Redis as their L2 storage.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureRedis">Optional Redis configuration.</param>
    /// <param name="configureStorage">Optional storage configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddRedisInfrastructure(
        this IServiceCollection services,
        Action<RedisOptions>? configureRedis = null,
        Action<StorageOptions>? configureStorage = null)
    {
        // Add core infrastructure
        services.AddCacheInfrastructure(configureStorage);

        // Configure Redis options
        if (configureRedis != null)
        {
            services.Configure(configureRedis);
        }

        // Register Redis connection services
        services.AddSingleton<RedisConnectionService>();
        services.AddHostedService<RedisConnectionService>(provider =>
            provider.GetRequiredService<RedisConnectionService>());

        // Register connection multiplexer
        services.AddSingleton<IConnectionMultiplexer>(provider =>
        {
            var connectionService = provider.GetRequiredService<RedisConnectionService>();
            var connectionTask = connectionService.GetConnectionAsync();

            try
            {
                return connectionTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                throw new TimeoutException("Redis connection initialization failed during service resolution", ex);
            }
        });

        // Register Redis-specific services
        services.AddSingleton<IRedisConnectionManager, RedisConnectionManager>();
        services.AddSingleton<IRedisSerializerFactory, RedisSerializerFactory>();
        services.AddSingleton<IRedisCompressionFactory, RedisCompressionFactory>();
        services.AddSingleton<IRedisSerializer>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<RedisOptions>>().Value;
            var serializerFactory = provider.GetRequiredService<IRedisSerializerFactory>();
            var compressionFactory = provider.GetRequiredService<IRedisCompressionFactory>();
            var logger = provider.GetRequiredService<ILogger<CompressedRedisSerializer>>();

            // Create base serializer
            var baseSerializer = serializerFactory.Create(options.DefaultSerializer);

            // Wrap with compression if enabled
            if (options.Compression != RedisCompressionType.None)
            {
                var compressor = compressionFactory.Create(options.Compression, options.CompressionThreshold);
                return new CompressedRedisSerializer(baseSerializer, compressor, logger);
            }

            return baseSerializer;
        });
        services.AddSingleton<IRedisTagManager, RedisTagManager>();

        // Register Redis infrastructure components
        services.TryAddSingleton<IStorageProvider, RedisStorageProvider>();
        services.TryAddSingleton<IBackplane, RedisBackplane>();

        return services;
    }

    /// <summary>
    /// Adds Redis infrastructure with hybrid storage manager.
    /// This provides the complete L1 (memory) + L2 (Redis) storage solution.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureRedis">Optional Redis configuration.</param>
    /// <param name="configureStorage">Optional storage configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddRedisHybridInfrastructure(
        this IServiceCollection services,
        Action<RedisOptions>? configureRedis = null,
        Action<StorageOptions>? configureStorage = null)
    {
        // Add Redis infrastructure
        services.AddRedisInfrastructure(configureRedis, configureStorage);

        // Add hybrid storage manager
        services.AddHybridStorageManager();

        return services;
    }

    /// <summary>
    /// Adds Redis infrastructure with connection string configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">Redis connection string.</param>
    /// <param name="configureRedis">Optional additional Redis configuration.</param>
    /// <param name="configureStorage">Optional storage configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddRedisInfrastructure(
        this IServiceCollection services,
        string connectionString,
        Action<RedisOptions>? configureRedis = null,
        Action<StorageOptions>? configureStorage = null)
    {
        return services.AddRedisInfrastructure(options =>
        {
            options.ConnectionString = connectionString;
            configureRedis?.Invoke(options);
        }, configureStorage);
    }

    /// <summary>
    /// Adds Redis infrastructure with health checks.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureRedis">Optional Redis configuration.</param>
    /// <param name="configureStorage">Optional storage configuration.</param>
    /// <param name="healthCheckName">Name for the health check.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddRedisInfrastructureWithHealthChecks(
        this IServiceCollection services,
        Action<RedisOptions>? configureRedis = null,
        Action<StorageOptions>? configureStorage = null,
        string healthCheckName = "redis_infrastructure")
    {
        services.AddRedisInfrastructure(configureRedis, configureStorage);

        // Add health checks
        services.AddHealthChecks()
            .AddCheck<RedisInfrastructureHealthCheck>(healthCheckName);

        services.AddSingleton<RedisInfrastructureHealthCheck>();

        return services;
    }

    /// <summary>
    /// Maps Redis options to Storage options for consistency.
    /// </summary>
    /// <param name="redisOptions">Redis options to map from.</param>
    /// <returns>Corresponding storage options.</returns>
    public static StorageOptions ToStorageOptions(this RedisOptions redisOptions)
    {
        return new StorageOptions
        {
            L2DefaultExpiration = redisOptions.DefaultExpiration,
            L2Enabled = true,
            EnableBackplane = redisOptions.EnablePubSubInvalidation,
            InstanceId = Environment.MachineName,
            KeyPrefix = redisOptions.KeyPrefix
        };
    }
}

/// <summary>
/// Health check for Redis infrastructure components.
/// </summary>
public class RedisInfrastructureHealthCheck : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    private readonly IStorageProvider _storageProvider;
    private readonly ILogger<RedisInfrastructureHealthCheck> _logger;

    public RedisInfrastructureHealthCheck(
        IStorageProvider storageProvider,
        ILogger<RedisInfrastructureHealthCheck> logger)
    {
        _storageProvider = storageProvider;
        _logger = logger;
    }

    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var healthStatus = await _storageProvider.GetHealthAsync(cancellationToken);
            var stats = await _storageProvider.GetStatsAsync(cancellationToken);

            var data = new Dictionary<string, object>
            {
                ["Provider"] = _storageProvider.Name,
                ["Status"] = healthStatus.ToString()
            };

            if (stats != null)
            {
                data["GetOperations"] = stats.GetOperations;
                data["SetOperations"] = stats.SetOperations;
                data["ErrorCount"] = stats.ErrorCount;
                data["AverageResponseTime"] = $"{stats.AverageResponseTimeMs:F2}ms";
            }

            return healthStatus switch
            {
                Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy =>
                    Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(
                        "Redis infrastructure is healthy", data),
                Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded =>
                    Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Degraded(
                        "Redis infrastructure is degraded", null, data),
                _ => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy(
                    "Redis infrastructure is unhealthy", null, data)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis infrastructure health check failed");
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy(
                "Redis infrastructure health check failed", ex);
        }
    }
}