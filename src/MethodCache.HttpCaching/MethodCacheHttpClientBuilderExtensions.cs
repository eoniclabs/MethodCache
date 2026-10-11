using System.Diagnostics.Metrics;
using MethodCache.HttpCaching;
using MethodCache.HttpCaching.Internal;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Adds HTTP caching to an <see cref="IHttpClientBuilder"/>.
/// </summary>
public static class MethodCacheHttpClientBuilderExtensions
{
    /// <summary>
    /// Caches this HttpClient's responses the way a browser does: it follows <c>Cache-Control</c> and <c>Vary</c>,
    /// revalidates with <c>ETag</c> / <c>Last-Modified</c> and turns a 304 into the cached response.
    /// </summary>
    /// <param name="builder">The HttpClient to cache.</param>
    /// <param name="configure">Settings for this HttpClient only.</param>
    /// <remarks>
    /// Responses are stored in memory. Set <see cref="HttpCacheStorageOptions.UseDistributedCache"/> to share them through
    /// Redis or SQL Server as well, or register your own <see cref="IHttpCacheStorage"/> to store them elsewhere.
    /// </remarks>
    public static IHttpClientBuilder AddHttpCaching(this IHttpClientBuilder builder, Action<HttpCacheOptions>? configure = null)
    {
        var services = builder.Services;
        var name = builder.Name ?? Microsoft.Extensions.Options.Options.DefaultName;

        services.AddOptions<HttpCacheStorageOptions>();
        services.TryAddSingleton<IHttpCacheStorage>(static sp =>
        {
            var storageOptions = sp.GetRequiredService<IOptions<HttpCacheStorageOptions>>();
            IDistributedCache? distributed = null;
            if (storageOptions.Value.UseDistributedCache)
            {
                distributed = sp.GetService<IDistributedCache>() ?? throw new InvalidOperationException(
                    "HttpCacheStorageOptions.UseDistributedCache is on, but no IDistributedCache is registered. " +
                    "Register one first, for example with AddMethodCacheRedis() or AddStackExchangeRedisCache().");
            }

            return new HttpCacheStore(storageOptions, sp.GetRequiredService<ILogger<HttpCacheStore>>(), sp.GetService<TimeProvider>(), distributed);
        });
        services.TryAddSingleton(static sp => new HttpCacheMetrics(sp.GetService<IMeterFactory>()));
        services.TryAddKeyedSingleton(name, (_, _) => new HttpCacheClientState(name));

        var options = services.AddOptions<HttpCacheOptions>(name);
        if (configure != null)
        {
            options.Configure(configure);
        }

        builder.AddHttpMessageHandler(sp => new HttpCacheHandler(
            sp.GetRequiredKeyedService<HttpCacheClientState>(name),
            sp.GetRequiredService<IHttpCacheStorage>(),
            sp.GetRequiredService<IOptionsMonitor<HttpCacheOptions>>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<ILogger<HttpCacheHandler>>(),
            sp.GetService<HttpCacheMetrics>()));

        return builder;
    }
}
