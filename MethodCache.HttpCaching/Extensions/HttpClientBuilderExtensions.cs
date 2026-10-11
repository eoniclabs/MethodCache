using MethodCache.HttpCaching.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MethodCache.HttpCaching.Metrics;
using MethodCache.HttpCaching.Storage;
using MethodCache.HttpCaching.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MethodCache.HttpCaching.Extensions;

public static class HttpClientBuilderExtensions
{
    /// <summary>
    /// Adds HTTP caching (ETag/Last-Modified revalidation, Cache-Control, Vary) to this HttpClient.
    /// Options set in <paramref name="configure"/> apply to this client only.
    /// </summary>
    public static IHttpClientBuilder AddHttpCaching(
        this IHttpClientBuilder builder,
        Action<HttpCachingBuilder>? configure = null)
    {
        var services = builder.Services;
        var name = builder.Name ?? Microsoft.Extensions.Options.Options.DefaultName;

        services.TryAddSingleton<IHttpCacheMetrics, HttpCacheMetrics>();
        services.TryAddSingleton<VaryHeaderCacheKeyGenerator>();
        services.TryAddKeyedSingleton(name, (_, _) => new HttpCacheClientState(name));

        var cachingBuilder = new HttpCachingBuilder(services, name);
        configure?.Invoke(cachingBuilder);
        cachingBuilder.Apply();

        builder.AddHttpMessageHandler(sp => new HttpCacheHandler(
            sp.GetRequiredKeyedService<HttpCacheClientState>(name),
            sp.GetRequiredService<IHttpCacheStorage>(),
            sp.GetRequiredService<IOptionsMonitor<HttpCacheOptions>>(),
            sp.GetRequiredService<VaryHeaderCacheKeyGenerator>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<ILogger<HttpCacheHandler>>(),
            sp.GetService<IHttpCacheMetrics>()));

        return builder;
    }
}
