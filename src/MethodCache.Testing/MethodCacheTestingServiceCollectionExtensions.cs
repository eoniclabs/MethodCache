using MethodCache.Core.Telemetry;
using MethodCache.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Sets up a container for tests of the caching itself.
/// </summary>
public static class MethodCacheTestingServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="FakeTimeProvider"/> as the container's <see cref="TimeProvider"/>, so a test moves the
    /// cache's clock with <see cref="FakeTimeProvider.Advance"/> instead of waiting, and a <see cref="CacheRecorder"/>
    /// that counts the loads and invalidations of the cache. Resolve both from the provider.
    /// </summary>
    /// <param name="services">The test's service collection, with <c>AddMethodCache()</c> before or after this call.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <remarks>
    /// The fake clock starts at 2000-01-01 and only moves when the test advances it, so nothing expires and no
    /// refresh-ahead runs on its own. It replaces a <see cref="TimeProvider"/> registered before this call.
    /// </remarks>
    public static IServiceCollection AddMethodCacheTesting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<FakeTimeProvider>();
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(static sp => sp.GetRequiredService<FakeTimeProvider>());
        services.TryAddSingleton(static sp => new CacheRecorder(sp));
        services.TryAddSingleton<ICacheEventSink>(static sp => sp.GetRequiredService<CacheRecorder>().Sink);
        return services;
    }
}
