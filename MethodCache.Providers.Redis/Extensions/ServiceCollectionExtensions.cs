using Microsoft.Extensions.DependencyInjection;
using MethodCache.Core;
using MethodCache.Infrastructure.Extensions;
using MethodCache.Providers.Redis.Configuration;
using MethodCache.Providers.Redis.Infrastructure;
using System;

namespace MethodCache.Providers.Redis.Extensions
{
    /// <summary>
    /// Extension methods for registering Redis cache services using Infrastructure layer.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Adds Redis cache services using Infrastructure layer.
        /// This registers Redis as both the distributed storage provider and as ICacheManager.
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

            // Register Redis as the primary cache manager through Infrastructure
            services.AddMemoryOnlyStorage();

            return services;
        }

        /// <summary>
        /// Adds Redis cache services with health checks.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configureOptions">Optional Redis configuration.</param>
        /// <param name="healthCheckName">Name for the health check.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddRedisCacheWithHealthChecks(
            this IServiceCollection services,
            Action<RedisOptions>? configureOptions = null,
            string healthCheckName = "redis_cache")
        {
            services.AddRedisInfrastructureWithHealthChecks(configureOptions, null, healthCheckName);
            services.AddMethodCache();
            services.AddMemoryOnlyStorage();

            return services;
        }

        /// <summary>
        /// Adds Redis cache services with health checks using connection string.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="connectionString">Redis connection string.</param>
        /// <param name="healthCheckName">Name for the health check.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddRedisCacheWithHealthChecks(
            this IServiceCollection services,
            string connectionString,
            string healthCheckName = "redis_cache")
        {
            return services.AddRedisCacheWithHealthChecks(options =>
            {
                options.ConnectionString = connectionString;
            }, healthCheckName);
        }

        /// <summary>
        /// Adds Redis cache services using connection string.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="connectionString">Redis connection string.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddRedisCache(
            this IServiceCollection services,
            string connectionString)
        {
            return services.AddRedisCache(options =>
            {
                options.ConnectionString = connectionString;
            });
        }

        /// <summary>
        /// Adds Redis cache services using connection string with additional configuration.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="connectionString">Redis connection string.</param>
        /// <param name="configureOptions">Additional Redis configuration.</param>
        /// <returns>The service collection for chaining.</returns>
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