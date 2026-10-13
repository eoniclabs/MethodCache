using System;
using System.Linq;
using MethodCache;
using MethodCache.Extensibility;
using MethodCache.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers MethodCache's OpenTelemetry instrumentation.
/// </summary>
public static class MethodCacheOpenTelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Adds tracing and metrics to MethodCache. Call this after <c>AddMethodCache()</c>, which registers the cache
    /// manager that this method instruments. Calling it more than once instruments the cache manager only once, and
    /// the options of every call, and of <c>Configure&lt;OpenTelemetryOptions&gt;</c>, apply in order. With
    /// <see cref="OpenTelemetryOptions.EnableMetrics"/> off, an <see cref="ICacheMetricsProvider"/> the application
    /// registered keeps receiving the cache's hits and misses.
    /// </summary>
    /// <exception cref="InvalidOperationException">No cache manager is registered yet.</exception>
    public static IServiceCollection AddMethodCacheOpenTelemetry(
        this IServiceCollection services,
        Action<OpenTelemetryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!services.Any(s => !s.IsKeyedService && s.ServiceType == typeof(ICacheManager)))
        {
            throw new InvalidOperationException(
                "No MethodCache cache manager is registered. Call services.AddMethodCache() before services.AddMethodCacheOpenTelemetry().");
        }

        var alreadyInstrumented = services.Any(s => s.ServiceType == typeof(OpenTelemetryInstrumentationMarker));

        // Only the values the caller sets, so earlier calls and Configure<OpenTelemetryOptions> keep theirs
        var options = services.AddOptions<OpenTelemetryOptions>();
        if (configure != null)
        {
            options.Configure(configure);
        }

        services.TryAddSingleton<ICacheActivitySource, CacheActivitySource>();
        services.TryAddSingleton<ICacheMeterProvider, CacheMeterProvider>();
        services.TryAddSingleton<IBaggagePropagator, BaggagePropagator>();

        if (alreadyInstrumented)
        {
            // The options above still apply; wrapping the cache manager again would duplicate every span.
            return services;
        }

        services.AddSingleton<OpenTelemetryInstrumentationMarker>();

        // With metrics off, the cache keeps reporting to the provider registered before, such as the application's own
        services.Decorate<ICacheMetricsProvider>((inner, provider) =>
            provider.GetRequiredService<IOptions<OpenTelemetryOptions>>().Value.EnableMetrics
                ? provider.GetRequiredService<ICacheMeterProvider>() as CacheMeterProvider
                  ?? throw new InvalidOperationException("CacheMeterProvider must implement ICacheMetricsProvider")
                : inner());

        services.Decorate<ICacheManager>((inner, provider) =>
        {
            var activitySource = provider.GetRequiredService<ICacheActivitySource>();
            var meterProvider = provider.GetRequiredService<ICacheMeterProvider>();
            var baggagePropagator = provider.GetRequiredService<IBaggagePropagator>();

            // Entry count and memory gauges read the cache's own statistics.
            if (provider.GetService<ICacheAdministrator>() is { } administrator && meterProvider is CacheMeterProvider cacheMeterProvider)
            {
                cacheMeterProvider.ObserveCache(administrator);
            }

            return new TelemetryCacheManager(inner(), activitySource, meterProvider, baggagePropagator);
        });

        return services;
    }

    /// <summary>Marks that the cache manager is already wrapped for telemetry.</summary>
    private sealed class OpenTelemetryInstrumentationMarker
    {
    }

    private static void Decorate<TInterface>(
        this IServiceCollection services,
        Func<Func<TInterface>, IServiceProvider, TInterface> decorator)
        where TInterface : class
    {
        var existingRegistrations = services.Where(s => !s.IsKeyedService && s.ServiceType == typeof(TInterface)).ToList();

        foreach (var registration in existingRegistrations)
        {
            services.Remove(registration);
            // Resolve the original descriptor through DI, so its lifetime and disposal ownership
            // survive decoration. An instance descriptor remains caller-owned.
            var originalKey = new object();
            if (registration.ImplementationFactory is { } factory)
            {
                services.Add(new ServiceDescriptor(typeof(TInterface), originalKey,
                    (provider, _) => factory(provider), registration.Lifetime));
            }
            else if (registration.ImplementationInstance is { } instance)
            {
                services.Add(new ServiceDescriptor(typeof(TInterface), originalKey, instance));
            }
            else
            {
                services.Add(new ServiceDescriptor(typeof(TInterface), originalKey,
                    registration.ImplementationType!, registration.Lifetime));
            }

            // The inner service is resolved only when the decorator asks for it
            object Factory(IServiceProvider provider) => decorator(() => provider.GetRequiredKeyedService<TInterface>(originalKey), provider);

            services.Add(new ServiceDescriptor(
                typeof(TInterface),
                Factory,
                registration.Lifetime));
        }
    }
}
