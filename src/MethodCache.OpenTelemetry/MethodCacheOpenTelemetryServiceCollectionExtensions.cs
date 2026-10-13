using System;
using System.Linq;
using MethodCache;
using MethodCache.Extensibility;
using MethodCache.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers MethodCache's OpenTelemetry instrumentation.
/// </summary>
public static class MethodCacheOpenTelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Adds tracing and metrics to MethodCache. Call this after <c>AddMethodCache()</c>, which registers the cache
    /// manager that this method instruments. Calling it more than once instruments the cache manager only once.
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

        var options = new OpenTelemetryOptions();
        configure?.Invoke(options);

        services.Configure<OpenTelemetryOptions>(opt =>
        {
            opt.EnableTracing = options.EnableTracing;
            opt.EnableMetrics = options.EnableMetrics;
            opt.RecordCacheKeys = options.RecordCacheKeys;
            opt.HashCacheKeys = options.HashCacheKeys;
            opt.RecordCacheTags = options.RecordCacheTags;
            opt.ExportSensitiveData = options.ExportSensitiveData;
            opt.EnableHttpCorrelation = options.EnableHttpCorrelation;
            opt.EnableBaggagePropagation = options.EnableBaggagePropagation;
        });

        services.TryAddSingleton<ICacheActivitySource, CacheActivitySource>();
        services.TryAddSingleton<ICacheMeterProvider, CacheMeterProvider>();
        services.TryAddSingleton<IBaggagePropagator, BaggagePropagator>();

        services.AddSingleton<ICacheMetricsProvider>(provider =>
            provider.GetRequiredService<ICacheMeterProvider>() as CacheMeterProvider
            ?? throw new InvalidOperationException("CacheMeterProvider must implement ICacheMetricsProvider"));

        if (alreadyInstrumented)
        {
            // The options above still apply; wrapping the cache manager again would duplicate every span.
            return services;
        }

        services.AddSingleton<OpenTelemetryInstrumentationMarker>();

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

            return new TelemetryCacheManager(inner, activitySource, meterProvider, baggagePropagator);
        });

        return services;
    }

    /// <summary>Marks that the cache manager is already wrapped for telemetry.</summary>
    private sealed class OpenTelemetryInstrumentationMarker
    {
    }

    private static void Decorate<TInterface>(
        this IServiceCollection services,
        Func<TInterface, IServiceProvider, TInterface> decorator)
        where TInterface : class
    {
        var existingRegistrations = services.Where(s => !s.IsKeyedService && s.ServiceType == typeof(TInterface)).ToList();

        foreach (var registration in existingRegistrations)
        {
            services.Remove(registration);

            object Factory(IServiceProvider provider)
            {
                TInterface instance;

                if (registration.ImplementationFactory != null)
                {
                    instance = (TInterface)registration.ImplementationFactory(provider);
                }
                else if (registration.ImplementationInstance != null)
                {
                    instance = (TInterface)registration.ImplementationInstance;
                }
                else if (registration.ImplementationType != null)
                {
                    instance = (TInterface)ActivatorUtilities.CreateInstance(provider, registration.ImplementationType);
                }
                else
                {
                    throw new InvalidOperationException($"Unable to resolve implementation for {typeof(TInterface)}");
                }

                return decorator(instance, provider);
            }

            services.Add(new ServiceDescriptor(
                typeof(TInterface),
                Factory,
                registration.Lifetime));
        }
    }
}
