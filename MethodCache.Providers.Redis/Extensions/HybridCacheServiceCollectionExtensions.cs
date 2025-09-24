using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MethodCache.Core;
using MethodCache.Core.Storage;
using MethodCache.Core.Runtime.Defaults;
using MethodCache.Infrastructure.Configuration;
using MethodCache.Providers.Redis.Configuration;
using MethodCache.Providers.Redis.Features;
using MethodCache.Providers.Redis.Infrastructure;
using System;

namespace MethodCache.Providers.Redis.Extensions
{
    public static class HybridCacheServiceCollectionExtensions
    {
        /// <summary>
        /// Adds hybrid L1/L2 cache with Redis as L2 and in-memory as L1.
        /// Uses the Infrastructure layer for provider-agnostic implementation.
        /// </summary>
        public static IServiceCollection AddHybridRedisCache(
            this IServiceCollection services,
            Action<HybridCacheOptions> configureHybridOptions,
            Action<RedisOptions>? configureRedisOptions = null)
        {
            if (configureHybridOptions == null)
                throw new ArgumentNullException(nameof(configureHybridOptions));

            // Add Redis infrastructure with L1+L2 storage
            services.AddRedisHybridInfrastructure(configureRedisOptions);

            // Configure hybrid cache options
            if (configureHybridOptions != null)
            {
                services.Configure(configureHybridOptions);
            }

            return services;
        }

        /// <summary>
        /// Adds hybrid L1/L2 cache with Redis and custom configuration.
        /// This is the comprehensive setup method for Redis hybrid caching.
        /// </summary>
        public static IServiceCollection AddHybridRedisCache(
            this IServiceCollection services,
            string connectionString,
            Action<HybridCacheOptions>? configureHybridOptions = null,
            Action<RedisOptions>? configureRedisOptions = null)
        {
            // Add Redis infrastructure
            services.AddRedisHybridInfrastructure(options =>
            {
                options.ConnectionString = connectionString;
                configureRedisOptions?.Invoke(options);
            });

            // Add hybrid cache using Infrastructure
            // Configure hybrid cache options
            if (configureHybridOptions != null)
            {
                services.Configure(configureHybridOptions);
            }

            return services;
        }


        /// <summary>
        /// Fluent configuration extension for hybrid cache options
        /// </summary>
        public static HybridCacheOptions WithL1Configuration(
            this HybridCacheOptions options,
            long maxItems = 10000,
            TimeSpan? defaultExpiration = null,
            MethodCache.Core.Storage.L1EvictionPolicy evictionPolicy = MethodCache.Core.Storage.L1EvictionPolicy.LRU)
        {
            options.L1MaxItems = maxItems;
            options.L1DefaultExpiration = defaultExpiration ?? TimeSpan.FromMinutes(5);
            options.L1EvictionPolicy = evictionPolicy;
            return options;
        }

        /// <summary>
        /// Fluent configuration extension for L2 cache options
        /// </summary>
        public static HybridCacheOptions WithL2Configuration(
            this HybridCacheOptions options,
            TimeSpan? defaultExpiration = null,
            bool enabled = true)
        {
            options.L2DefaultExpiration = defaultExpiration ?? TimeSpan.FromHours(4);
            options.L2Enabled = enabled;
            return options;
        }

        /// <summary>
        /// Fluent configuration extension for hybrid strategy
        /// </summary>
        public static HybridCacheOptions WithStrategy(
            this HybridCacheOptions options,
            HybridStrategy strategy,
            bool enableL1Warming = true,
            bool enableAsyncL2Writes = true)
        {
            options.Strategy = strategy;
            options.EnableL1Warming = enableL1Warming;
            options.EnableAsyncL2Writes = enableAsyncL2Writes;
            return options;
        }

        /// <summary>
        /// Fluent configuration extension for performance settings
        /// </summary>
        public static HybridCacheOptions WithPerformanceSettings(
            this HybridCacheOptions options,
            int maxConcurrentL2Operations = 10)
        {
            options.MaxConcurrentL2Operations = maxConcurrentL2Operations;
            return options;
        }

        /// <summary>
        /// Fluent configuration extension for advanced features
        /// </summary>
        public static HybridCacheOptions WithAdvancedFeatures(
            this HybridCacheOptions options,
            bool enableBackplane = true)
        {
            options.EnableBackplane = enableBackplane;
            return options;
        }

        /// <summary>
        /// Adds the complete Redis + Hybrid Cache stack in one call.
        /// This is the recommended way to set up hybrid caching with Redis using Infrastructure.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="connectionString">Redis connection string.</param>
        /// <param name="configure">Optional configuration action.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddRedisHybridCacheComplete(
            this IServiceCollection services,
            string connectionString,
            Action<RedisHybridCacheOptions>? configure = null)
        {
            var options = new RedisHybridCacheOptions();
            configure?.Invoke(options);

            // Add Redis infrastructure
            services.AddRedisHybridInfrastructure(redis =>
            {
                redis.ConnectionString = connectionString;
                redis.Compression = options.RedisCompression;
                redis.DefaultSerializer = options.RedisSerializer;
                redis.EnablePubSubInvalidation = options.EnableBackplane;
            }, storage =>
            {
                storage.L1DefaultExpiration = options.L1DefaultExpiration;
                storage.L1MaxExpiration = options.L1MaxExpiration;
                storage.L2DefaultExpiration = options.L2DefaultExpiration;
                storage.EnableBackplane = options.EnableBackplane;
            });

            // Add hybrid cache configuration
            services.Configure<HybridCacheOptions>(hybrid =>
            {
                hybrid.L1DefaultExpiration = options.L1DefaultExpiration;
                hybrid.L1MaxExpiration = options.L1MaxExpiration;
                hybrid.L2DefaultExpiration = options.L2DefaultExpiration;
                hybrid.L2Enabled = true;
                hybrid.Strategy = options.Strategy;
                hybrid.EnableBackplane = options.EnableBackplane;
                hybrid.EnableAsyncL2Writes = options.EnableAsyncL2Writes;
            });

            return services;
        }
    }

    /// <summary>
    /// Configuration options for Redis + Hybrid Cache setup.
    /// </summary>
    public class RedisHybridCacheOptions
    {
        /// <summary>
        /// Default expiration time for L1 cache entries.
        /// </summary>
        public TimeSpan L1DefaultExpiration { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Maximum expiration time for L1 cache entries.
        /// </summary>
        public TimeSpan L1MaxExpiration { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Default expiration time for L2 cache entries.
        /// </summary>
        public TimeSpan L2DefaultExpiration { get; set; } = TimeSpan.FromHours(4);

        /// <summary>
        /// Hybrid cache strategy.
        /// </summary>
        public HybridStrategy Strategy { get; set; } = HybridStrategy.WriteThrough;

        /// <summary>
        /// Whether to enable backplane coordination.
        /// </summary>
        public bool EnableBackplane { get; set; } = true;

        /// <summary>
        /// Whether to enable async L2 writes.
        /// </summary>
        public bool EnableAsyncL2Writes { get; set; } = true;

        /// <summary>
        /// Redis compression type.
        /// </summary>
        public RedisCompressionType RedisCompression { get; set; } = RedisCompressionType.Gzip;

        /// <summary>
        /// Redis serializer type.
        /// </summary>
        public RedisSerializerType RedisSerializer { get; set; } = RedisSerializerType.MessagePack;
    }
}
