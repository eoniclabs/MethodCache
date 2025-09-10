using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MethodCache.Core;
using MethodCache.Providers.Redis.Configuration;
using MethodCache.Providers.Redis.Features;
using MethodCache.Providers.Redis.Compression;
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
            
            // Register Redis connection
            services.AddSingleton<IConnectionMultiplexer>(provider =>
            {
                var options = provider.GetRequiredService<IOptions<RedisOptions>>().Value;
                var config = ConfigurationOptions.Parse(options.ConnectionString);
                config.ConnectTimeout = (int)options.ConnectTimeout.TotalMilliseconds;
                config.SyncTimeout = (int)options.SyncTimeout.TotalMilliseconds;
                
                return ConnectionMultiplexer.Connect(config);
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
            services.AddSingleton<IRedisPubSubInvalidation, RedisPubSubInvalidation>();
            services.AddSingleton<ICacheWarmingService, RedisCacheWarmingService>();
            
            // Register cache warming as hosted service if enabled
            services.AddSingleton<RedisCacheWarmingService>(provider => 
                (RedisCacheWarmingService)provider.GetRequiredService<ICacheWarmingService>());
            services.AddHostedService<RedisCacheWarmingService>(provider => 
                provider.GetRequiredService<RedisCacheWarmingService>());
            
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