using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MethodCache.Providers.Redis.MultiRegion;
using System;

namespace MethodCache.Providers.Redis.Extensions
{
    public static class MultiRegionServiceCollectionExtensions
    {
        public static IServiceCollection AddMultiRegionRedisCache(
            this IServiceCollection services,
            Action<MultiRegionOptions> configureOptions)
        {
            if (configureOptions == null)
                throw new ArgumentNullException(nameof(configureOptions));

            // Configure multi-region options
            services.Configure(configureOptions);

            // Register multi-region services
            services.AddSingleton<IRegionSelector>(provider =>
            {
                var options = provider.GetRequiredService<IOptions<MultiRegionOptions>>().Value;
                return new RegionSelector(options);
            });

            services.AddSingleton<IMultiRegionCacheManager, MultiRegionCacheManager>();

            // Add health checks
            services.AddHealthChecks()
                .AddCheck<MultiRegionHealthCheckService>(
                    name: "multi_region_redis_cache",
                    tags: new[] { "redis", "cache", "multi-region" });

            return services;
        }

        public static IServiceCollection AddMultiRegionRedisCache(
            this IServiceCollection services,
            Action<MultiRegionOptions> configureOptions,
            string healthCheckName)
        {
            services.AddMultiRegionRedisCache(configureOptions);

            // Replace default health check name
            services.AddHealthChecks()
                .AddCheck<MultiRegionHealthCheckService>(
                    name: healthCheckName,
                    tags: new[] { "redis", "cache", "multi-region" });

            return services;
        }

        public static MultiRegionOptions AddRegion(
            this MultiRegionOptions options,
            string name,
            string connectionString,
            bool isPrimary = false,
            int priority = 0,
            RegionReplicationStrategy replicationStrategy = RegionReplicationStrategy.Eventually)
        {
            options.Regions.Add(new RegionConfiguration
            {
                Name = name,
                ConnectionString = connectionString,
                IsPrimary = isPrimary,
                Priority = priority,
                ReplicationStrategy = replicationStrategy
            });

            if (isPrimary && string.IsNullOrEmpty(options.PrimaryRegion))
            {
                options.PrimaryRegion = name;
            }

            return options;
        }

        public static MultiRegionOptions WithFailoverStrategy(
            this MultiRegionOptions options,
            RegionFailoverStrategy strategy)
        {
            options.FailoverStrategy = strategy;
            return options;
        }

        public static MultiRegionOptions WithCrossRegionSync(
            this MultiRegionOptions options,
            TimeSpan syncInterval,
            int maxConcurrentSyncs = 3)
        {
            options.CrossRegionSyncInterval = syncInterval;
            options.MaxConcurrentSyncs = maxConcurrentSyncs;
            return options;
        }

        public static MultiRegionOptions EnableCrossRegionInvalidation(
            this MultiRegionOptions options,
            bool enable = true)
        {
            options.EnableCrossRegionInvalidation = enable;
            return options;
        }

        public static MultiRegionOptions EnableRegionAffinity(
            this MultiRegionOptions options,
            bool enable = true)
        {
            options.EnableRegionAffinity = enable;
            return options;
        }
    }
}