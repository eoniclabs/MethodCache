using MethodCache;
using MethodCache.Core.Runtime.Http;
using MethodCache.Core.Runtime.Distributed;
using MethodCache.Core.Telemetry;
using MethodCache.HttpCaching;
using MethodCache.HttpCaching.Internal;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Http;
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
    /// Caching runs after all additional handlers, so their request transformations and caller identity are visible.
    /// Automatic cookies are disabled on this client's SocketsHttpHandler or HttpClientHandler after all handler configuration.
    /// Send caller cookies explicitly so they participate in cache isolation. Unknown transports bypass caching unless explicitly trusted.
    /// Requires AddMethodCache. Core stores responses in its separate HTTP memory budget, or exclusively in
    /// MethodCache's native Redis/SQL Server transport when configured. KeepCookies preserves transport cookies
    /// and bypasses response caching completely.
    /// <para>
    /// A client has at most one cache: calling this for every client (in <c>ConfigureHttpClientDefaults</c>) and again
    /// for one client, or twice for one client, adds the cache once and applies every <paramref name="configure"/>.
    /// With caching added for every client, set one client's options with
    /// <c>services.Configure&lt;HttpCacheOptions&gt;("name", ...)</c>.
    /// </para>
    /// </remarks>
    public static IHttpClientBuilder AddHttpCaching(this IHttpClientBuilder builder, Action<HttpCacheOptions>? configure = null)
    {
        var services = builder.Services;
        var name = builder.Name ?? Microsoft.Extensions.Options.Options.DefaultName;

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, HttpCacheTransportFilter>());
        // The outermost filter finishes last, after logging and application filters have added their handlers.
        var transportFilter = services.First(descriptor => descriptor.ServiceType == typeof(IHttpMessageHandlerBuilderFilter)
            && descriptor.ImplementationType == typeof(HttpCacheTransportFilter));
        services.Remove(transportFilter);
        services.Insert(0, transportFilter);
        services.AddLogging();
        services.TryAddSingleton(static sp =>
        {
            if (sp.GetService<MethodCacheServiceCollectionExtensions.ExplicitCoreRegistration>() == null)
                throw new InvalidOperationException("AddHttpCaching requires an explicit AddMethodCache registration in this container.");
            return new HttpResponseStore(sp.GetRequiredService<IOptions<MethodCacheMemoryOptions>>(),
                sp.GetRequiredService<CoreTelemetry>(), sp.GetService<TimeProvider>(), sp.GetService<DistributedCacheLayer>(), HttpCacheEntrySerializer.FormatVersion);
        });
        services.TryAddSingleton<IHttpCacheStorage>(static sp => new HttpCacheStore(
            sp.GetRequiredService<HttpResponseStore>(), sp.GetService<TimeProvider>()));
        services.TryAddSingleton<ICacheAdministrator>(MethodCacheServiceCollectionExtensions.CreateAdministrator);
        services.AddOptions<HttpStartupValidation>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<HttpStartupValidation>, HttpStartupValidator>());
        services.TryAddSingleton<HttpCacheClientStates>();
        services.TryAddKeyedSingleton(name, (sp, _) => sp.GetRequiredService<HttpCacheClientStates>().For(name));

        var options = services.AddOptions<HttpCacheOptions>(name)
            .Validate(static options => options.Tags.Count <= 125 && options.Tags.All(static tag => !string.IsNullOrWhiteSpace(tag) && tag.Length <= 1024),
                "HTTP static tags must be nonempty, at most 1024 code units each, with at most 125 custom tags.")
            .Validate(static options => options.MaxResponseBytes > 0,
                "HttpCacheOptions.MaxResponseBytes must be positive; set long.MaxValue for no limit.")
            .Validate(static options => (options.DefaultMaxAge == null || options.DefaultMaxAge >= TimeSpan.Zero) &&
                options.MaxHeuristicFreshness >= TimeSpan.Zero && options.StaleRetention >= TimeSpan.Zero &&
                options.MaxStaleIfError >= TimeSpan.Zero,
                "HTTP freshness and retention durations must be non-negative.")
            .Validate(static options => options.BackgroundRevalidationTimeout > TimeSpan.Zero &&
                options.BackgroundRevalidationTimeout <= TimeSpan.FromMilliseconds(uint.MaxValue - 1),
                "HTTP background revalidation timeout must be positive and fit the platform timer range.")
            .ValidateOnStart();
        if (configure != null)
        {
            if (builder.Name == null) services.ConfigureAll(configure);
            else options.Configure(configure);
        }

        builder.AddHttpMessageHandler(sp => new HttpCacheHandler(
            sp.GetRequiredKeyedService<HttpCacheClientState>(name),
            sp.GetRequiredService<IHttpCacheStorage>(),
            sp.GetRequiredService<IOptionsMonitor<HttpCacheOptions>>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<ILogger<HttpCacheHandler>>(),
            sp.GetService<CoreTelemetry>()));

        return builder;
    }
}

internal sealed class HttpStartupValidation;
internal sealed class HttpStartupValidator(IServiceProvider services) : IValidateOptions<HttpStartupValidation>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, HttpStartupValidation options)
    {
        _ = services.GetRequiredService<HttpResponseStore>();
        return ValidateOptionsResult.Success;
    }
}
