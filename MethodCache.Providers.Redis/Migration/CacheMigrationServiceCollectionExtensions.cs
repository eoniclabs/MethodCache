using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MethodCache.Providers.Redis.Migration
{
    public static class CacheMigrationServiceCollectionExtensions
    {
        /// <summary>
        /// Adds cache migration services to the service collection
        /// </summary>
        public static IServiceCollection AddCacheMigration(this IServiceCollection services)
        {
            services.TryAddSingleton<ICacheMigrationTool, RedisCacheMigrationTool>();
            
            return services;
        }

        /// <summary>
        /// Adds cache migration services with custom implementation
        /// </summary>
        public static IServiceCollection AddCacheMigration<T>(this IServiceCollection services)
            where T : class, ICacheMigrationTool
        {
            services.TryAddSingleton<ICacheMigrationTool, T>();
            
            return services;
        }
    }
}