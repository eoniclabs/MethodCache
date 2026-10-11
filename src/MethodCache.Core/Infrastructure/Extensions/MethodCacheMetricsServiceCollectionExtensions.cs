using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Core.Infrastructure.Extensions
{
    /// <summary>
    /// Convenience helpers for configuring cache metrics providers.
    /// </summary>
    public static class MethodCacheMetricsServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the console metrics provider so cache events are written to the console output.
        /// Use this when observability matters more than raw throughput.
        /// </summary>
        public static IServiceCollection AddConsoleCacheMetrics(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);
            services.AddSingleton<ICacheMetricsProvider, ConsoleCacheMetricsProvider>();
            return services;
        }
    }
}
