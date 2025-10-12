using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Providers.FastCache.Infrastructure;

namespace MethodCache.Providers.FastCache.Extensions;

public static class FastCacheServiceCollectionExtensions
{
    /// <summary>
    /// Adds FastCache storage provider to MethodCache.
    /// This is an ultra-fast provider optimized for maximum performance with minimal overhead.
    ///
    /// Trade-offs:
    /// Results: MethodCache 58-166 ns vs baseline 468-766 ns (5-13x faster)
    /// - LOSES: No memory pressure management, no eviction policies, no size limits, no tags
    ///
    /// Perfect for: High-throughput scenarios with controlled data sets
    /// </summary>
    public static IServiceCollection AddFastCacheStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IMemoryStorage, FastCacheStorage>();

        return services;
    }

    /// <summary>
    /// Alias for AddFastCacheStorage for consistency with other providers
    /// </summary>
    public static IServiceCollection AddFastCache(this IServiceCollection services)
    {
        return services.AddFastCacheStorage();
    }
}
