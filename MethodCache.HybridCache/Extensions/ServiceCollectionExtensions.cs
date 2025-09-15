using System;
using MethodCache.Core;
using MethodCache.HybridCache.Abstractions;
using MethodCache.HybridCache.Configuration;
using MethodCache.HybridCache.Implementation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MethodCache.HybridCache.Extensions
{
    /// <summary>
    /// Extension methods for configuring hybrid cache services with explicit, predictable dependency injection.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Adds hybrid cache services with in-memory L1 cache only (L2 disabled).
        /// This registers HybridCacheManager as the primary ICacheManager.
        /// </summary>
        public static IServiceCollection AddHybridCache(
            this IServiceCollection services,
            Action<HybridCacheOptions>? configureOptions = null)
        {
            // Configure options with L2 disabled
            services.Configure<HybridCacheOptions>(options =>
            {
                options.L2Enabled = false;
                options.Strategy = HybridStrategy.L1Only;
                configureOptions?.Invoke(options);
            });
            
            // Register L1 cache
            services.TryAddSingleton<IMemoryCache, InMemoryCacheManager>();
            
            // Register hybrid cache manager (no L2 dependency)
            services.TryAddSingleton<IHybridCacheManager>(provider => 
                new HybridCacheManager(
                    provider.GetRequiredService<IMemoryCache>(),
                    null!, // No L2 cache
                    provider.GetService<ICacheBackplane>(),
                    provider.GetRequiredService<IOptions<HybridCacheOptions>>(),
                    provider.GetRequiredService<ILogger<HybridCacheManager>>()
                ));
            
            // Register as primary cache manager
            services.TryAddSingleton<ICacheManager>(provider => 
                provider.GetRequiredService<IHybridCacheManager>());
            
            return services;
        }
        
        /// <summary>
        /// Adds hybrid cache with both L1 and L2 layers.
        /// The L2 cache must be registered separately using AddL2Cache{T}().
        /// </summary>
        public static IServiceCollection AddHybridCache<TL2Cache>(
            this IServiceCollection services,
            Action<HybridCacheOptions>? configureOptions = null)
            where TL2Cache : class, ICacheManager
        {
            // Configure options with L2 enabled
            services.Configure<HybridCacheOptions>(options =>
            {
                options.L2Enabled = true;
                configureOptions?.Invoke(options);
            });
            
            // Register L1 cache
            services.TryAddSingleton<IMemoryCache, InMemoryCacheManager>();
            
            // Register L2 cache with explicit keyed registration to prevent circular dependency
            services.TryAddSingleton<TL2Cache>();
            services.TryAddKeyedSingleton<ICacheManager, TL2Cache>("L2Cache");
            
            // Register hybrid cache manager with explicit L2 dependency
            services.TryAddSingleton<IHybridCacheManager>(provider => 
                new HybridCacheManager(
                    provider.GetRequiredService<IMemoryCache>(),
                    provider.GetRequiredKeyedService<ICacheManager>("L2Cache"),
                    provider.GetService<ICacheBackplane>(),
                    provider.GetRequiredService<IOptions<HybridCacheOptions>>(),
                    provider.GetRequiredService<ILogger<HybridCacheManager>>()
                ));
            
            // Register as primary cache manager
            services.TryAddSingleton<ICacheManager>(provider => 
                provider.GetRequiredService<IHybridCacheManager>());
            
            return services;
        }
        
        /// <summary>
        /// Adds hybrid cache with custom L1 cache implementation.
        /// </summary>
        public static IServiceCollection AddHybridCache<TL1Cache, TL2Cache>(
            this IServiceCollection services,
            Action<HybridCacheOptions>? configureOptions = null)
            where TL1Cache : class, IMemoryCache
            where TL2Cache : class, ICacheManager
        {
            // Configure options with L2 enabled
            services.Configure<HybridCacheOptions>(options =>
            {
                options.L2Enabled = true;
                configureOptions?.Invoke(options);
            });
            
            // Register custom L1 cache
            services.TryAddSingleton<IMemoryCache, TL1Cache>();
            
            // Register L2 cache with keyed registration
            services.TryAddSingleton<TL2Cache>();
            services.TryAddKeyedSingleton<ICacheManager, TL2Cache>("L2Cache");
            
            // Register hybrid cache manager
            services.TryAddSingleton<IHybridCacheManager>(provider => 
                new HybridCacheManager(
                    provider.GetRequiredService<IMemoryCache>(),
                    provider.GetRequiredKeyedService<ICacheManager>("L2Cache"),
                    provider.GetService<ICacheBackplane>(),
                    provider.GetRequiredService<IOptions<HybridCacheOptions>>(),
                    provider.GetRequiredService<ILogger<HybridCacheManager>>()
                ));
            
            // Register as primary cache manager
            services.TryAddSingleton<ICacheManager>(provider => 
                provider.GetRequiredService<IHybridCacheManager>());
            
            return services;
        }
        
        /// <summary>
        /// Adds a cache backplane for cross-instance invalidation.
        /// </summary>
        public static IServiceCollection AddHybridBackplane<TBackplane>(
            this IServiceCollection services)
            where TBackplane : class, ICacheBackplane
        {
            services.TryAddSingleton<ICacheBackplane, TBackplane>();
            return services;
        }
        
        /// <summary>
        /// Adds hybrid cache using a fluent builder pattern.
        /// The builder collects all configuration and applies it atomically in Build().
        /// </summary>
        public static HybridCacheBuilder AddHybridCacheBuilder(this IServiceCollection services)
        {
            return new HybridCacheBuilder(services);
        }
    }
    
    /// <summary>
    /// Builder for configuring hybrid cache with explicit, predictable service registration.
    /// Collects all configuration and applies it atomically to prevent partial registration.
    /// </summary>
    public class HybridCacheBuilder
    {
        private readonly IServiceCollection _services;
        private readonly HybridCacheOptions _options;
        private Type? _l1CacheType;
        private Type? _l2CacheType;
        private Type? _backplaneType;
        
        internal HybridCacheBuilder(IServiceCollection services)
        {
            _services = services;
            _options = new HybridCacheOptions();
        }
        
        /// <summary>
        /// Configures the L1 (in-memory) cache implementation.
        /// </summary>
        public HybridCacheBuilder WithL1Cache<TL1Cache>() where TL1Cache : class, IMemoryCache
        {
            _l1CacheType = typeof(TL1Cache);
            return this;
        }
        
        /// <summary>
        /// Configures the L2 (distributed) cache implementation.
        /// </summary>
        public HybridCacheBuilder WithL2Cache<TL2Cache>() where TL2Cache : class, ICacheManager
        {
            _l2CacheType = typeof(TL2Cache);
            _options.L2Enabled = true;
            return this;
        }
        
        /// <summary>
        /// Configures the backplane for cross-instance invalidation.
        /// </summary>
        public HybridCacheBuilder WithBackplane<TBackplane>() where TBackplane : class, ICacheBackplane
        {
            _backplaneType = typeof(TBackplane);
            _options.EnableBackplane = true;
            return this;
        }
        
        /// <summary>
        /// Configures hybrid cache options.
        /// </summary>
        public HybridCacheBuilder WithOptions(Action<HybridCacheOptions> configure)
        {
            configure(_options);
            return this;
        }
        
        /// <summary>
        /// Applies all collected configuration to the service collection atomically.
        /// This ensures either all services are registered or none (no partial state).
        /// </summary>
        public IServiceCollection Build()
        {
            // Apply options configuration
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
            
            // Register L1 cache
            if (_l1CacheType != null)
            {
                _services.TryAdd(ServiceDescriptor.Singleton(typeof(IMemoryCache), _l1CacheType));
            }
            else
            {
                _services.TryAddSingleton<IMemoryCache, InMemoryCacheManager>();
            }
            
            // Register L2 cache with keyed registration if specified
            if (_l2CacheType != null)
            {
                _services.TryAdd(ServiceDescriptor.Singleton(_l2CacheType, _l2CacheType));
                _services.TryAdd(ServiceDescriptor.KeyedSingleton(typeof(ICacheManager), "L2Cache", (provider, key) =>
                    provider.GetRequiredService(_l2CacheType)));
            }
            
            // Register backplane if specified
            if (_backplaneType != null)
            {
                _services.TryAdd(ServiceDescriptor.Singleton(typeof(ICacheBackplane), _backplaneType));
            }
            
            // Register hybrid cache manager with explicit dependencies
            _services.TryAddSingleton<IHybridCacheManager>(provider => 
            {
                var l1Cache = provider.GetRequiredService<IMemoryCache>();
                var l2Cache = _l2CacheType != null ? provider.GetRequiredKeyedService<ICacheManager>("L2Cache") : null!;
                var backplane = provider.GetService<ICacheBackplane>();
                var options = provider.GetRequiredService<IOptions<HybridCacheOptions>>();
                var logger = provider.GetRequiredService<ILogger<HybridCacheManager>>();
                
                return new HybridCacheManager(l1Cache, l2Cache, backplane, options, logger);
            });
            
            // Register as primary cache manager
            _services.TryAddSingleton<ICacheManager>(provider => 
                provider.GetRequiredService<IHybridCacheManager>());
            
            return _services;
        }
    }
}