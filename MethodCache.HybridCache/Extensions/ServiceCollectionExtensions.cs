using System;
using MethodCache.Core;
using MethodCache.HybridCache.Abstractions;
using MethodCache.HybridCache.Configuration;
using MethodCache.HybridCache.Implementation;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.HybridCache.Extensions
{
    /// <summary>
    /// Extension methods for configuring hybrid cache services.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Adds hybrid cache services with the specified L2 cache and backplane.
        /// </summary>
        public static IServiceCollection AddHybridCache(
            this IServiceCollection services,
            Action<HybridCacheOptions>? configureOptions = null)
        {
            // Configure options
            if (configureOptions != null)
            {
                services.Configure(configureOptions);
            }
            else
            {
                services.Configure<HybridCacheOptions>(_ => { });
            }
            
            // Register L1 cache
            services.AddSingleton<IL1Cache, MemoryL1Cache>();
            
            // Register hybrid cache manager as both IHybridCacheManager and ICacheManager
            services.AddSingleton<IHybridCacheManager, HybridCacheManager>();
            services.AddSingleton<ICacheManager>(provider => provider.GetRequiredService<IHybridCacheManager>());
            
            return services;
        }
        
        /// <summary>
        /// Adds hybrid cache with custom L1 cache implementation.
        /// </summary>
        public static IServiceCollection AddHybridCache<TL1Cache>(
            this IServiceCollection services,
            Action<HybridCacheOptions>? configureOptions = null)
            where TL1Cache : class, IL1Cache
        {
            // Configure options
            if (configureOptions != null)
            {
                services.Configure(configureOptions);
            }
            else
            {
                services.Configure<HybridCacheOptions>(_ => { });
            }
            
            // Register custom L1 cache
            services.AddSingleton<IL1Cache, TL1Cache>();
            
            // Register hybrid cache manager
            services.AddSingleton<IHybridCacheManager, HybridCacheManager>();
            services.AddSingleton<ICacheManager>(provider => provider.GetRequiredService<IHybridCacheManager>());
            
            return services;
        }
        
        /// <summary>
        /// Configures the L2 cache for hybrid caching.
        /// Must be called BEFORE AddHybridCache.
        /// </summary>
        public static IServiceCollection AddHybridL2Cache<TL2Cache>(
            this IServiceCollection services)
            where TL2Cache : class, ICacheManager
        {
            // Register L2 cache with a specific key so hybrid cache can find it
            services.AddSingleton<TL2Cache>();
            services.AddSingleton<ICacheManager>(provider => 
            {
                // Check if we're in hybrid mode
                var hybridManager = provider.GetService<IHybridCacheManager>();
                if (hybridManager != null)
                {
                    // Return the hybrid manager as the primary ICacheManager
                    return hybridManager;
                }
                
                // Otherwise return the L2 cache directly
                return provider.GetRequiredService<TL2Cache>();
            });
            
            return services;
        }
        
        /// <summary>
        /// Configures the backplane for hybrid caching.
        /// </summary>
        public static IServiceCollection AddHybridBackplane<TBackplane>(
            this IServiceCollection services)
            where TBackplane : class, ICacheBackplane
        {
            services.AddSingleton<ICacheBackplane, TBackplane>();
            return services;
        }
        
        /// <summary>
        /// Builder for configuring hybrid cache.
        /// </summary>
        public class HybridCacheBuilder
        {
            private readonly IServiceCollection _services;
            private readonly HybridCacheOptions _options;
            
            public HybridCacheBuilder(IServiceCollection services)
            {
                _services = services;
                _options = new HybridCacheOptions();
            }
            
            public HybridCacheBuilder WithL1Cache<TL1Cache>() where TL1Cache : class, IL1Cache
            {
                _services.AddSingleton<IL1Cache, TL1Cache>();
                return this;
            }
            
            public HybridCacheBuilder WithL2Cache<TL2Cache>() where TL2Cache : class, ICacheManager
            {
                _services.AddSingleton<TL2Cache>();
                // Store the L2 cache type for later resolution
                _services.AddSingleton<ICacheManager>(provider =>
                {
                    var hybrid = provider.GetService<HybridCacheManager>();
                    return hybrid ?? (ICacheManager)provider.GetRequiredService<TL2Cache>();
                });
                return this;
            }
            
            public HybridCacheBuilder WithBackplane<TBackplane>() where TBackplane : class, ICacheBackplane
            {
                _services.AddSingleton<ICacheBackplane, TBackplane>();
                _options.EnableBackplane = true;
                return this;
            }
            
            public HybridCacheBuilder WithOptions(Action<HybridCacheOptions> configure)
            {
                configure(_options);
                return this;
            }
            
            public IServiceCollection Build()
            {
                _services.Configure<HybridCacheOptions>(opt =>
                {
                    opt.Strategy = _options.Strategy;
                    opt.L1DefaultExpiration = _options.L1DefaultExpiration;
                    opt.L1MaxExpiration = _options.L1MaxExpiration;
                    opt.L2DefaultExpiration = _options.L2DefaultExpiration;
                    opt.L2Enabled = _options.L2Enabled;
                    opt.L1MaxItems = _options.L1MaxItems;
                    opt.L1MaxMemoryBytes = _options.L1MaxMemoryBytes;
                    opt.EnableL1Warming = _options.EnableL1Warming;
                    opt.EnableAsyncL2Writes = _options.EnableAsyncL2Writes;
                    opt.MaxConcurrentL2Operations = _options.MaxConcurrentL2Operations;
                    opt.L1EvictionPolicy = _options.L1EvictionPolicy;
                    opt.EnableBackplane = _options.EnableBackplane;
                    opt.InstanceId = _options.InstanceId;
                    opt.EnableDebugLogging = _options.EnableDebugLogging;
                    opt.L2RetryPolicy = _options.L2RetryPolicy;
                });
                
                // Register hybrid cache manager
                _services.AddSingleton<IHybridCacheManager, HybridCacheManager>();
                
                return _services;
            }
        }
        
        /// <summary>
        /// Adds hybrid cache using a fluent builder pattern.
        /// </summary>
        public static HybridCacheBuilder AddHybridCacheBuilder(this IServiceCollection services)
        {
            return new HybridCacheBuilder(services);
        }
    }
}