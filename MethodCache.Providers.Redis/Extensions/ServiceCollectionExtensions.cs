using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MethodCache.Core;
using MethodCache.HybridCache.Abstractions;
using MethodCache.Providers.Redis.Backplane;
using MethodCache.Providers.Redis.Configuration;
using MethodCache.Providers.Redis.Features;
using MethodCache.Providers.Redis.Compression;
using MethodCache.Providers.Redis.Services;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;

namespace MethodCache.Providers.Redis.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddRedisCache(
            this IServiceCollection services,
            Action<RedisOptions>? configureOptions = null)
        {
            // Ensure MethodCache core is registered
            services.AddMethodCache();
            
            // Configure options
            if (configureOptions != null)
            {
                services.Configure(configureOptions);
            }
            
            // Register Redis connection service (async initialization)
            services.AddSingleton<RedisConnectionService>();
            services.AddHostedService<RedisConnectionService>(provider => 
                provider.GetRequiredService<RedisConnectionService>());
            
            // Register connection multiplexer that waits for async initialization with timeout
            services.AddSingleton<IConnectionMultiplexer>(provider =>
            {
                var connectionService = provider.GetRequiredService<RedisConnectionService>();
                var connectionTask = connectionService.GetConnectionAsync();
                
                // Use GetAwaiter().GetResult() for better async context handling and longer timeout
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
            services.AddSingleton<IDistributedLock, RedisDistributedLock>();
            
            // Register the comprehensive backplane for invalidation (replaces duplicate pub/sub mechanism)
            services.AddSingleton<ICacheBackplane, RedisCacheBackplane>();
            
            // Register legacy pub/sub interface as wrapper around backplane for backward compatibility
            services.AddSingleton<IRedisPubSubInvalidation, RedisPubSubInvalidation>();
            
            services.AddSingleton<ICacheWarmingService, RedisCacheWarmingService>();
            
            // Register cache warming as hosted service only if enabled
            services.AddSingleton<RedisCacheWarmingService>(provider => 
                (RedisCacheWarmingService)provider.GetRequiredService<ICacheWarmingService>());
            
            // Conditionally register hosted service based on configuration
            services.AddSingleton<IHostedService>(provider =>
            {
                var options = provider.GetRequiredService<IOptions<RedisOptions>>().Value;
                if (options.EnableCacheWarming)
                {
                    return provider.GetRequiredService<RedisCacheWarmingService>();
                }
                
                // Return a no-op hosted service when warming is disabled
                return new NoOpHostedService();
            });
            
            // Replace the default cache manager with Redis implementation
            services.Replace(ServiceDescriptor.Singleton<ICacheManager, RedisCacheManager>());
            
            return services;
        }

        public static IServiceCollection AddRedisCacheWithHealthChecks(
            this IServiceCollection services,
            Action<RedisOptions>? configureOptions = null,
            string healthCheckName = "redis_cache")
        {
            services.AddRedisCache(configureOptions);
            services.AddRedisHealthChecks(healthCheckName);
            
            return services;
        }
        
        public static IServiceCollection AddRedisCacheWithHealthChecks(
            this IServiceCollection services,
            string connectionString,
            string healthCheckName = "redis_cache")
        {
            services.AddRedisCache(connectionString);
            services.AddRedisHealthChecks(healthCheckName);
            
            return services;
        }
        
        public static IServiceCollection AddRedisCache(
            this IServiceCollection services,
            string connectionString)
        {
            return services.AddRedisCache(options =>
            {
                options.ConnectionString = connectionString;
            });
        }
        
        public static IServiceCollection AddRedisCache(
            this IServiceCollection services,
            string connectionString,
            Action<RedisOptions> configureOptions)
        {
            return services.AddRedisCache(options =>
            {
                options.ConnectionString = connectionString;
                configureOptions(options);
            });
        }
    }
}