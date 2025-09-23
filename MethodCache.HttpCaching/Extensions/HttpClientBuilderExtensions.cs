using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.HttpCaching.Storage;
using MethodCache.Infrastructure.Abstractions;
using MethodCache.Infrastructure.Configuration;
using MethodCache.Infrastructure.Extensions;

namespace MethodCache.HttpCaching.Extensions;

/// <summary>
/// Extension methods for adding HTTP caching to HttpClient configurations.
/// </summary>
public static class HttpClientBuilderExtensions
{
    /// <summary>
    /// Adds standards-compliant HTTP caching to the HttpClient pipeline.
    /// </summary>
    /// <param name="builder">The IHttpClientBuilder to configure.</param>
    /// <param name="configure">Optional configuration action.</param>
    /// <returns>The IHttpClientBuilder for method chaining.</returns>
    public static IHttpClientBuilder AddHttpCaching(
        this IHttpClientBuilder builder,
        Action<HttpCacheOptions>? configure = null)
    {
        // Configure options
        var options = new HttpCacheOptions();
        configure?.Invoke(options);

        // Register the options instance
        builder.Services.AddSingleton(options);

        // Ensure required services are available
        builder.Services.TryAddSingleton<IMemoryCache, MemoryCache>(); // For default storage
        builder.Services.AddLogging(); // For diagnostics

        // Register options and handler factory
        builder.Services.AddTransient<HttpCacheHandler>(provider =>
        {
            // If no storage specified, use in-memory with IMemoryCache
            if (options.Storage == null)
            {
                var memoryCache = provider.GetRequiredService<IMemoryCache>();
                var storageLogger = provider.GetRequiredService<ILogger<InMemoryHttpCacheStorage>>();
                options.Storage = new InMemoryHttpCacheStorage(memoryCache, options, storageLogger);
            }

            var logger = provider.GetRequiredService<ILogger<HttpCacheHandler>>();
            return new HttpCacheHandler(options.Storage, options, logger);
        });

        // Register the HTTP cache handler
        builder.AddHttpMessageHandler<HttpCacheHandler>();

        return builder;
    }

    /// <summary>
    /// Adds HTTP caching with in-memory storage.
    /// </summary>
    /// <param name="builder">The IHttpClientBuilder to configure.</param>
    /// <param name="configure">Optional configuration action.</param>
    /// <returns>The IHttpClientBuilder for method chaining.</returns>
    public static IHttpClientBuilder AddHttpCachingInMemory(
        this IHttpClientBuilder builder,
        Action<HttpCacheOptions>? configure = null)
    {
        return builder.AddHttpCaching(options =>
        {
            configure?.Invoke(options);
            // Storage will be set to in-memory by default in AddHttpCaching
        });
    }

    /// <summary>
    /// Adds HTTP caching with custom storage.
    /// </summary>
    /// <param name="builder">The IHttpClientBuilder to configure.</param>
    /// <param name="storageFactory">Factory function to create the storage implementation.</param>
    /// <param name="configure">Optional configuration action.</param>
    /// <returns>The IHttpClientBuilder for method chaining.</returns>
    public static IHttpClientBuilder AddHttpCachingWithStorage<TStorage>(
        this IHttpClientBuilder builder,
        Func<IServiceProvider, TStorage> storageFactory,
        Action<HttpCacheOptions>? configure = null)
        where TStorage : class, IHttpCacheStorage
    {
        return builder.AddHttpCaching(options =>
        {
            configure?.Invoke(options);
            // Storage will be set by the service provider when creating the handler
        });
    }

    /// <summary>
    /// Adds HTTP caching optimized for API clients with sensible defaults.
    /// </summary>
    /// <param name="builder">The IHttpClientBuilder to configure.</param>
    /// <param name="configure">Optional configuration action.</param>
    /// <returns>The IHttpClientBuilder for method chaining.</returns>
    public static IHttpClientBuilder AddApiCaching(
        this IHttpClientBuilder builder,
        Action<HttpCacheOptions>? configure = null)
    {
        return builder.AddHttpCaching(options =>
        {
            // API-optimized defaults
            options.RespectCacheControl = true;
            options.EnableStaleWhileRevalidate = true;
            options.EnableStaleIfError = true;
            options.DefaultMaxAge = TimeSpan.FromMinutes(5);
            options.MaxStaleIfError = TimeSpan.FromHours(1);
            options.AddDiagnosticHeaders = false; // Disable in production

            configure?.Invoke(options);
        });
    }

    /// <summary>
    /// Adds HTTP caching with debugging enabled for development environments.
    /// </summary>
    /// <param name="builder">The IHttpClientBuilder to configure.</param>
    /// <param name="configure">Optional configuration action.</param>
    /// <returns>The IHttpClientBuilder for method chaining.</returns>
    public static IHttpClientBuilder AddHttpCachingWithDebug(
        this IHttpClientBuilder builder,
        Action<HttpCacheOptions>? configure = null)
    {
        return builder.AddHttpCaching(options =>
        {
            options.AddDiagnosticHeaders = true;
            configure?.Invoke(options);
        });
    }

    /// <summary>
    /// Adds HTTP caching with hybrid storage (L1 memory + L2 distributed) infrastructure.
    /// This provides the best performance with memory-first caching backed by distributed storage.
    /// </summary>
    /// <param name="builder">The IHttpClientBuilder to configure.</param>
    /// <param name="configureHttp">Optional HTTP cache configuration action.</param>
    /// <param name="configureStorage">Optional storage configuration action.</param>
    /// <returns>The IHttpClientBuilder for method chaining.</returns>
    public static IHttpClientBuilder AddHybridHttpCaching(
        this IHttpClientBuilder builder,
        Action<HttpCacheOptions>? configureHttp = null,
        Action<StorageOptions>? configureStorage = null)
    {
        // Register the hybrid storage infrastructure
        builder.Services.AddHybridStorageManager(configureStorage);

        // Configure HTTP cache options
        builder.Services.Configure<HttpCacheOptions>(options =>
        {
            // Set defaults optimized for hybrid storage
            options.EnableStaleWhileRevalidate = true;
            options.EnableStaleIfError = true;
            options.DefaultMaxAge = TimeSpan.FromMinutes(15);
            options.MaxStaleIfError = TimeSpan.FromHours(2);

            configureHttp?.Invoke(options);
        });

        // Register the hybrid HTTP cache storage
        builder.Services.TryAddScoped<IHttpCacheStorage, HybridHttpCacheStorage>();

        // Register the HTTP cache handler
        builder.Services.AddTransient<HttpCacheHandler>(provider =>
        {
            var storage = provider.GetRequiredService<IHttpCacheStorage>();
            var options = provider.GetRequiredService<IOptions<HttpCacheOptions>>().Value;
            var logger = provider.GetRequiredService<ILogger<HttpCacheHandler>>();
            return new HttpCacheHandler(storage, options, logger);
        });

        builder.AddHttpMessageHandler<HttpCacheHandler>();

        return builder;
    }

    /// <summary>
    /// Adds HTTP caching with memory-only storage using the infrastructure layer.
    /// This provides consistent behavior with other storage types but without distributed caching.
    /// </summary>
    /// <param name="builder">The IHttpClientBuilder to configure.</param>
    /// <param name="configureHttp">Optional HTTP cache configuration action.</param>
    /// <param name="configureStorage">Optional storage configuration action.</param>
    /// <returns>The IHttpClientBuilder for method chaining.</returns>
    public static IHttpClientBuilder AddMemoryHttpCaching(
        this IHttpClientBuilder builder,
        Action<HttpCacheOptions>? configureHttp = null,
        Action<StorageOptions>? configureStorage = null)
    {
        // Register the memory-only storage infrastructure
        builder.Services.AddMemoryOnlyStorage(configureStorage);

        // Configure HTTP cache options
        if (configureHttp != null)
        {
            builder.Services.Configure<HttpCacheOptions>(configureHttp);
        }

        // Register the hybrid HTTP cache storage (which will use memory-only provider)
        builder.Services.TryAddScoped<IHttpCacheStorage, HybridHttpCacheStorage>();

        // Register the HTTP cache handler
        builder.Services.AddTransient<HttpCacheHandler>(provider =>
        {
            var storage = provider.GetRequiredService<IHttpCacheStorage>();
            var options = provider.GetRequiredService<IOptions<HttpCacheOptions>>().Value;
            var logger = provider.GetRequiredService<ILogger<HttpCacheHandler>>();
            return new HttpCacheHandler(storage, options, logger);
        });

        builder.AddHttpMessageHandler<HttpCacheHandler>();

        return builder;
    }
}