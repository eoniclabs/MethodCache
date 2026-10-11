using MethodCache.HttpCaching.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MethodCache.HttpCaching;

public static class HttpClientBuilderExtensions
{
    /// <summary>
    /// Caches this HttpClient's responses the way a browser does: it follows <c>Cache-Control</c> and <c>Vary</c>,
    /// revalidates with <c>ETag</c> / <c>Last-Modified</c> and turns a 304 into the cached response.
    /// </summary>
    /// <param name="builder">The HttpClient to cache.</param>
    /// <param name="configure">Settings for this HttpClient only.</param>
    /// <remarks>
    /// Responses are stored in memory by default. Register your own <see cref="IHttpCacheStorage"/> to store them elsewhere.
    /// </remarks>
    public static IHttpClientBuilder AddHttpCaching(this IHttpClientBuilder builder, Action<HttpCacheOptions>? configure = null)
    {
        var services = builder.Services;
        var name = builder.Name ?? Microsoft.Extensions.Options.Options.DefaultName;

        services.AddOptions<HttpCacheStorageOptions>();
        services.TryAddSingleton<IHttpCacheStorage, InMemoryHttpCacheStorage>();
        services.TryAddSingleton<HttpCacheMetrics>();
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
