using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using MethodCache.Core;
using MethodCache.Core.Infrastructure;
using MethodCache.Core.Runtime;
using MethodCache.Core.Storage;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.OpenTelemetry.Configuration;
using MethodCache.OpenTelemetry.Instrumentation;
using MethodCache.OpenTelemetry.Metrics;
using MethodCache.OpenTelemetry.Propagators;
using MethodCache.OpenTelemetry.Tracing;

namespace MethodCache.OpenTelemetry.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMethodCacheOpenTelemetry(
        this IServiceCollection services,
        Action<OpenTelemetryOptions>? configure = null)
    {
        var options = new OpenTelemetryOptions();
        configure?.Invoke(options);
        options.Validate();

        services.Configure<OpenTelemetryOptions>(opt =>
        {
            opt.EnableTracing = options.EnableTracing;
            opt.EnableMetrics = options.EnableMetrics;
            opt.RecordCacheKeys = options.RecordCacheKeys;
            opt.HashCacheKeys = options.HashCacheKeys;
            opt.SamplingRatio = options.SamplingRatio;
            opt.ExportSensitiveData = options.ExportSensitiveData;
            opt.MetricExportInterval = options.MetricExportInterval;
            opt.EnableHttpCorrelation = options.EnableHttpCorrelation;
            opt.EnableBaggagePropagation = options.EnableBaggagePropagation;
            opt.EnableDistributedTracing = options.EnableDistributedTracing;
            opt.EnableStorageProviderInstrumentation = options.EnableStorageProviderInstrumentation;
            opt.ServiceName = options.ServiceName;
            opt.ServiceVersion = options.ServiceVersion;
            opt.ServiceNamespace = options.ServiceNamespace;
            opt.Environment = options.Environment;
        });

        services.TryAddSingleton<ICacheActivitySource, CacheActivitySource>();
        services.TryAddSingleton<ICacheMeterProvider, CacheMeterProvider>();
        services.TryAddSingleton<IBaggagePropagator, BaggagePropagator>();

        services.AddSingleton<ICacheMetricsProvider>(provider =>
            provider.GetRequiredService<ICacheMeterProvider>() as CacheMeterProvider
            ?? throw new InvalidOperationException("CacheMeterProvider must implement ICacheMetricsProvider"));

        if (options.EnableHttpCorrelation)
        {
            services.AddHttpContextAccessor();
            services.TryAddSingleton<IHttpCorrelationEnricher, HttpCorrelationEnricher>();
        }

        services.Decorate<ICacheManager>((inner, provider) =>
        {
            var activitySource = provider.GetRequiredService<ICacheActivitySource>();
            var meterProvider = provider.GetRequiredService<ICacheMeterProvider>();
            var baggagePropagator = provider.GetRequiredService<IBaggagePropagator>();
            return new TelemetryCacheManager(inner, activitySource, meterProvider, baggagePropagator);
        });

        if (options.EnableStorageProviderInstrumentation)
        {
            services.Decorate<IStorageProvider>((inner, provider) =>
            {
                var activitySource = provider.GetRequiredService<ICacheActivitySource>();
                var meterProvider = provider.GetRequiredService<ICacheMeterProvider>();
                var providerName = inner.GetType().Name.Replace("StorageProvider", "").Replace("Storage", "");
                return new InstrumentedStorageProvider(inner, activitySource, meterProvider, providerName);
            });
        }

        return services;
    }

    public static IServiceCollection AddOpenTelemetryWithMethodCache(
        this IServiceCollection services,
        Action<OpenTelemetryOptions>? configureMethodCache = null)
    {
        services.AddMethodCacheOpenTelemetry(configureMethodCache);

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: "MethodCache",
                    serviceVersion: TracingConstants.ActivitySourceVersion))
            .WithTracing(tracing => tracing
                .AddMethodCacheInstrumentation())
            .WithMetrics(metrics => metrics
                .AddMethodCacheInstrumentation());

        return services;
    }

    public static TracerProviderBuilder AddMethodCacheInstrumentation(
        this TracerProviderBuilder builder)
    {
        builder.AddSource(TracingConstants.ActivitySourceName);
        return builder;
    }

    public static MeterProviderBuilder AddMethodCacheInstrumentation(
        this MeterProviderBuilder builder)
    {
        builder.AddMeter(MetricInstruments.MeterName);
        return builder;
    }

    public static IApplicationBuilder UseMethodCacheHttpCorrelation(
        this IApplicationBuilder app)
    {
        app.UseMiddleware<HttpCorrelationMiddleware>();
        return app;
    }

    private static void Decorate<TInterface>(
        this IServiceCollection services,
        Func<TInterface, IServiceProvider, TInterface> decorator)
        where TInterface : class
    {
        var existingRegistrations = services.Where(s => s.ServiceType == typeof(TInterface)).ToList();

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