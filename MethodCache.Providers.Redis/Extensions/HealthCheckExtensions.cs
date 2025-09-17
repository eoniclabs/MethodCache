using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MethodCache.Providers.Redis.HealthChecks;
using System;

namespace MethodCache.Providers.Redis.Extensions
{
    public static class HealthCheckExtensions
    {
        public static IServiceCollection AddRedisHealthChecks(this IServiceCollection services, string name = "redis_cache")
        {
            services.AddSingleton<RedisHealthCheck>();
            services.AddHealthChecks()
                .AddCheck<RedisHealthCheck>(
                    name: name,
                    failureStatus: HealthStatus.Unhealthy,
                    tags: new[] { "redis", "cache", "database" });

            return services;
        }

        public static IServiceCollection AddRedisHealthChecks(this IServiceCollection services, 
            string name, 
            HealthStatus failureStatus, 
            TimeSpan timeout,
            params string[] tags)
        {
            services.AddSingleton<RedisHealthCheck>();
            services.AddHealthChecks()
                .AddCheck<RedisHealthCheck>(
                    name: name,
                    failureStatus: failureStatus,
                    timeout: timeout,
                    tags: tags);

            return services;
        }
    }
}