using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MethodCache.Core;
using MethodCache.Providers.Redis.Configuration;
using MethodCache.Providers.Redis.Features;
using MethodCache.Providers.Redis.Backplane;
using MethodCache.HybridCache.Abstractions;
using MethodCache.HybridCache.Configuration;
using MethodCache.HybridCache.Implementation;
using MethodCache.HybridCache.Extensions;
using System;

namespace MethodCache.Providers.Redis.Extensions
{
    public static class HybridCacheServiceCollectionExtensions
    {
        /// <summary>
        /// Adds hybrid L1/L2 cache with Redis as L2 and in-memory as L1
        /// </summary>
        public static IServiceCollection AddHybridRedisCache(
            this IServiceCollection services,
            Action<HybridCacheOptions> configureHybridOptions,
            Action<RedisOptions>? configureRedisOptions = null)
        {
            if (configureHybridOptions == null)
                throw new ArgumentNullException(nameof(configureHybridOptions));

            // Configure hybrid cache options
            services.Configure(configureHybridOptions);

            // Add Redis infrastructure first
            services.AddRedisCache(configureRedisOptions ?? (_ => { }));

            // Store the original Redis cache manager under a different key
            services.AddSingleton<RedisCacheManager>(provider =>
            {
                var connectionManager = provider.GetRequiredService<IRedisConnectionManager>();
                var serializer = provider.GetRequiredService<IRedisSerializer>();
                var tagManager = provider.GetRequiredService<IRedisTagManager>();
                var distributedLock = provider.GetRequiredService<IDistributedLock>();
                var pubSubInvalidation = provider.GetRequiredService<IRedisPubSubInvalidation>();
                var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<RedisOptions>>();
                var metricsProvider = provider.GetRequiredService<ICacheMetricsProvider>();
                var logger = provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RedisCacheManager>>();
                
                return new RedisCacheManager(connectionManager, serializer, tagManager, distributedLock, pubSubInvalidation, metricsProvider, options, logger);
            });

            // Register Redis backplane for hybrid cache
            services.AddSingleton<ICacheBackplane, RedisCacheBackplane>();
            
            // Register L1 cache
            services.AddSingleton<IL1Cache, MemoryL1Cache>();

            // Register hybrid cache manager
            services.AddSingleton<IHybridCacheManager>(provider =>
            {
                var l1Cache = provider.GetRequiredService<IL1Cache>();
                var l2Cache = provider.GetRequiredService<RedisCacheManager>(); // Use dedicated Redis instance
                var backplane = provider.GetRequiredService<ICacheBackplane>();
                var hybridOptions = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<HybridCacheOptions>>();
                var logger = provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<HybridCacheManager>>();

                return new HybridCacheManager(l1Cache, l2Cache, backplane, hybridOptions, logger);
            });

            // Replace ICacheManager with hybrid implementation
            services.Replace(ServiceDescriptor.Singleton<ICacheManager>(provider =>
                provider.GetRequiredService<IHybridCacheManager>()));

            return services;
        }

        /// <summary>
        /// Adds hybrid cache with custom L2 cache implementation
        /// </summary>
        public static IServiceCollection AddHybridCache<TL2Cache>(
            this IServiceCollection services,
            Action<HybridCacheOptions> configureHybridOptions)
            where TL2Cache : class, ICacheManager
        {
            if (configureHybridOptions == null)
                throw new ArgumentNullException(nameof(configureHybridOptions));

            // Configure hybrid cache options
            services.Configure(configureHybridOptions);

            // Register L1 cache
            services.AddSingleton<IL1Cache, MemoryL1Cache>();

            // Register custom L2 cache
            services.AddSingleton<TL2Cache>();
            
            // Note: For generic L2 cache, backplane would need to be configured separately
            // Register hybrid cache manager with null backplane
            services.AddSingleton<IHybridCacheManager>(provider =>
            {
                var l1Cache = provider.GetRequiredService<IL1Cache>();
                var l2Cache = provider.GetRequiredService<TL2Cache>();
                var backplane = provider.GetService<ICacheBackplane>(); // Optional
                var hybridOptions = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<HybridCacheOptions>>();
                var logger = provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<HybridCacheManager>>();

                return new HybridCacheManager(l1Cache, l2Cache, backplane, hybridOptions, logger);
            });

            // Replace ICacheManager with hybrid implementation
            services.Replace(ServiceDescriptor.Singleton<ICacheManager>(provider =>
                provider.GetRequiredService<IHybridCacheManager>()));

            return services;
        }

        /// <summary>
        /// Fluent configuration extension for hybrid cache options
        /// </summary>
        public static HybridCacheOptions WithL1Configuration(
            this HybridCacheOptions options,
            long maxItems = 10000,
            TimeSpan? defaultExpiration = null,
            L1EvictionPolicy evictionPolicy = L1EvictionPolicy.LRU)
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
    }
}