using MethodCache.ETags.Abstractions;
using MethodCache.ETags.Implementation;
using MethodCache.ETags.Middleware;
using MethodCache.HybridCache.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MethodCache.ETags.Extensions
{
    /// <summary>
    /// Extension methods for configuring ETag support in the service collection.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Adds ETag support to the existing hybrid cache configuration.
        /// Requires IHybridCacheManager to be already registered.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="configureOptions">Optional ETag middleware configuration</param>
        /// <returns>The service collection for method chaining</returns>
        public static IServiceCollection AddETagSupport(
            this IServiceCollection services,
            Action<ETagMiddlewareOptions>? configureOptions = null)
        {
            // Ensure hybrid cache is registered
            var hybridCacheDescriptor = services.FirstOrDefault(s => s.ServiceType == typeof(IHybridCacheManager));
            if (hybridCacheDescriptor == null)
            {
                throw new InvalidOperationException(
                    "IHybridCacheManager must be registered before adding ETag support. " +
                    "Call AddHybridCache() first.");
            }

            // Register ETag cache manager
            services.TryAddSingleton<IETagCacheManager, ETagHybridCacheManager>();

            // Configure middleware options
            if (configureOptions != null)
            {
                services.Configure(configureOptions);
            }
            else
            {
                services.Configure<ETagMiddlewareOptions>(_ => { });
            }

            return services;
        }

        /// <summary>
        /// Adds ETag support with custom ETag cache manager implementation.
        /// </summary>
        /// <typeparam name="TETagCacheManager">Custom ETag cache manager type</typeparam>
        /// <param name="services">The service collection</param>
        /// <param name="configureOptions">Optional ETag middleware configuration</param>
        /// <returns>The service collection for method chaining</returns>
        public static IServiceCollection AddETagSupport<TETagCacheManager>(
            this IServiceCollection services,
            Action<ETagMiddlewareOptions>? configureOptions = null)
            where TETagCacheManager : class, IETagCacheManager
        {
            // Register custom ETag cache manager
            services.TryAddSingleton<IETagCacheManager, TETagCacheManager>();

            // Configure middleware options
            if (configureOptions != null)
            {
                services.Configure(configureOptions);
            }

            return services;
        }

        /// <summary>
        /// Adds ETag backplane support for cross-instance ETag invalidation.
        /// </summary>
        /// <typeparam name="TETagBackplane">ETag backplane implementation type</typeparam>
        /// <param name="services">The service collection</param>
        /// <returns>The service collection for method chaining</returns>
        public static IServiceCollection AddETagBackplane<TETagBackplane>(
            this IServiceCollection services)
            where TETagBackplane : class, IETagCacheBackplane
        {
            services.TryAddSingleton<IETagCacheBackplane, TETagBackplane>();
            return services;
        }

        /// <summary>
        /// Configures ETag middleware options.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="configureOptions">Configuration action</param>
        /// <returns>The service collection for method chaining</returns>
        public static IServiceCollection ConfigureETagMiddleware(
            this IServiceCollection services,
            Action<ETagMiddlewareOptions> configureOptions)
        {
            services.Configure(configureOptions);
            return services;
        }
    }

    /// <summary>
    /// Builder for configuring ETag support with fluent interface.
    /// </summary>
    public class ETagBuilder
    {
        private readonly IServiceCollection _services;

        internal ETagBuilder(IServiceCollection services)
        {
            _services = services;
        }

        /// <summary>
        /// Configures ETag middleware options.
        /// </summary>
        /// <param name="configureOptions">Configuration action</param>
        /// <returns>This builder for method chaining</returns>
        public ETagBuilder WithMiddlewareOptions(Action<ETagMiddlewareOptions> configureOptions)
        {
            _services.Configure(configureOptions);
            return this;
        }

        /// <summary>
        /// Adds ETag backplane for cross-instance invalidation.
        /// </summary>
        /// <typeparam name="TETagBackplane">Backplane implementation type</typeparam>
        /// <returns>This builder for method chaining</returns>
        public ETagBuilder WithBackplane<TETagBackplane>()
            where TETagBackplane : class, IETagCacheBackplane
        {
            _services.TryAddSingleton<IETagCacheBackplane, TETagBackplane>();
            return this;
        }

        /// <summary>
        /// Uses a custom ETag cache manager implementation.
        /// </summary>
        /// <typeparam name="TETagCacheManager">Custom ETag cache manager type</typeparam>
        /// <returns>This builder for method chaining</returns>
        public ETagBuilder WithETagCacheManager<TETagCacheManager>()
            where TETagCacheManager : class, IETagCacheManager
        {
            _services.AddSingleton<IETagCacheManager, TETagCacheManager>();
            return this;
        }

        /// <summary>
        /// Finalizes the ETag builder configuration.
        /// </summary>
        /// <returns>The service collection</returns>
        public IServiceCollection Build()
        {
            return _services;
        }
    }

    /// <summary>
    /// Extension methods for fluent ETag configuration.
    /// </summary>
    public static class ETagBuilderExtensions
    {
        /// <summary>
        /// Creates an ETag builder for fluent configuration.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <returns>ETag builder instance</returns>
        public static ETagBuilder AddETagBuilder(this IServiceCollection services)
        {
            return new ETagBuilder(services);
        }

        /// <summary>
        /// Adds ETag support and returns a builder for additional configuration.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <returns>ETag builder instance</returns>
        public static ETagBuilder AddETagSupportBuilder(this IServiceCollection services)
        {
            services.AddETagSupport();
            return new ETagBuilder(services);
        }
    }
}