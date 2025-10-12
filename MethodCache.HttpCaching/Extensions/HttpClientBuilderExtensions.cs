using MethodCache.HttpCaching.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MethodCache.HttpCaching.Metrics;
using MethodCache.HttpCaching.Options;
using MethodCache.HttpCaching.Storage;
using MethodCache.HttpCaching.Validation;
using Microsoft.Extensions.Options;

namespace MethodCache.HttpCaching.Extensions;

public static class HttpClientBuilderExtensions
{
    public static IHttpClientBuilder AddHttpCaching(
        this IHttpClientBuilder builder,
        Action<HttpCachingBuilder>? configure = null)
    {
        builder.Services.TryAddSingleton<IMemoryCache>(sp =>
        {
            var storageOptions = sp.GetRequiredService<IOptions<HttpCacheStorageOptions>>().Value;
            var memoryOptions = new MemoryCacheOptions();

            if (storageOptions.MaxCacheSize > 0)
            {
                memoryOptions.SizeLimit = storageOptions.MaxCacheSize;
            }

            return new MemoryCache(memoryOptions);
        });
        builder.Services.TryAddSingleton<IHttpCacheMetrics, HttpCacheMetrics>();
        builder.Services.TryAddSingleton<VaryHeaderCacheKeyGenerator>();

        var cachingBuilder = new HttpCachingBuilder(builder.Services);
        configure?.Invoke(cachingBuilder);
        cachingBuilder.Apply();

        builder.Services.AddTransient<HttpCacheHandler>();
        builder.AddHttpMessageHandler<HttpCacheHandler>();

        return builder;
    }
}
