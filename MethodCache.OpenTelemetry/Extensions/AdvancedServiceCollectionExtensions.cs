using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MethodCache.OpenTelemetry.Configuration;
using MethodCache.OpenTelemetry.Correlation;
using MethodCache.OpenTelemetry.Exporters;
using MethodCache.OpenTelemetry.HotReload;
using MethodCache.OpenTelemetry.Security;
using MethodCache.OpenTelemetry.Tracing;

namespace MethodCache.OpenTelemetry.Extensions;

/// <summary>
/// Advanced service collection extensions for enhanced OpenTelemetry features
/// </summary>
public static class AdvancedServiceCollectionExtensions
{
    /// <summary>
    /// Adds advanced OpenTelemetry features with security, correlation, and hot reload support
    /// </summary>
    public static IServiceCollection AddAdvancedMethodCacheOpenTelemetry(
        this IServiceCollection services,
        Action<OpenTelemetryOptions>? configureOpenTelemetry = null,
        Action<SecurityOptions>? configureSecurity = null,
        Action<AdvancedCorrelationOptions>? configureCorrelation = null,
        bool enableHotReload = true)
    {
        // Add base OpenTelemetry support
        services.AddMethodCacheOpenTelemetry(configureOpenTelemetry);

        // Configure Security options
        if (configureSecurity != null)
        {
            services.Configure<SecurityOptions>(configureSecurity);
        }

        // Configure Correlation options
        if (configureCorrelation != null)
        {
            services.Configure<AdvancedCorrelationOptions>(configureCorrelation);
        }

        // Add security services
        services.TryAddSingleton<IPIIDetector, RegexPIIDetector>();
        services.TryAddSingleton<IPIIRedactor, PIIRedactor>();

        // Decorate the activity source with security enhancement
        DecorateService<ICacheActivitySource>(services, (inner, provider) =>
        {
            var securityOptions = provider.GetService<Microsoft.Extensions.Options.IOptions<SecurityOptions>>();
            if (securityOptions?.Value.EnablePIIDetection == true)
            {
                var detector = provider.GetService<IPIIDetector>();
                var redactor = provider.GetService<IPIIRedactor>();
                var encryptor = provider.GetService<IAttributeEncryptor>();
                return new SecurityEnhancedActivitySource(inner, securityOptions, detector, redactor, encryptor);
            }
            return inner;
        });

        // Add advanced correlation services
        services.TryAddSingleton<IAdvancedCorrelationManager, AdvancedCorrelationManager>();

        // Add exporter factory
        services.TryAddSingleton<ICacheMetricsExporterFactory, CacheMetricsExporterFactory>();

        // Add hot reload support if enabled
        if (enableHotReload)
        {
            services.AddHotReloadSupport();
        }

        return services;
    }

    /// <summary>
    /// Adds hot reload support for OpenTelemetry configuration
    /// </summary>
    public static IServiceCollection AddHotReloadSupport(this IServiceCollection services)
    {
        services.TryAddSingleton<IConfigurationReloadManager>(provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var logger = provider.GetService<Microsoft.Extensions.Logging.ILogger<ConfigurationReloadManager>>();

            // Try to find appsettings.json path for file watching
            var environment = provider.GetService<IHostEnvironment>();
            var configPath = environment != null
                ? System.IO.Path.Combine(environment.ContentRootPath, "appsettings.json")
                : null;

            return new ConfigurationReloadManager(configuration, logger!, configPath);
        });

        // Register as hosted service for proper lifecycle management
        services.AddHostedService<HotReloadHostedService>();

        return services;
    }

    /// <summary>
    /// Configures advanced correlation middleware
    /// </summary>
    public static IApplicationBuilder UseAdvancedCorrelation(this IApplicationBuilder app)
    {
        app.UseMiddleware<CorrelationMiddleware>();
        return app;
    }

    /// <summary>
    /// Registers a custom metrics exporter
    /// </summary>
    public static IServiceCollection AddCustomMetricsExporter<T>(
        this IServiceCollection services,
        string name,
        Func<IServiceProvider, T> factory)
        where T : class, ICacheMetricsExporter
    {
        services.AddSingleton<T>(factory);

        // Register with the exporter factory
        services.Configure<CacheMetricsExporterFactoryOptions>(options =>
        {
            options.RegisterExporter(name, provider =>
            {
                var exporter = provider.GetRequiredService<T>();
                return exporter;
            });
        });

        return services;
    }

    /// <summary>
    /// Registers a custom PII detector
    /// </summary>
    public static IServiceCollection AddCustomPIIDetector<T>(this IServiceCollection services)
        where T : class, IPIIDetector
    {
        services.TryAddSingleton<IPIIDetector, T>();
        return services;
    }

    /// <summary>
    /// Registers a custom attribute encryptor
    /// </summary>
    public static IServiceCollection AddAttributeEncryption<T>(this IServiceCollection services)
        where T : class, IAttributeEncryptor
    {
        services.TryAddSingleton<IAttributeEncryptor, T>();
        return services;
    }

    /// <summary>
    /// Configures security options from configuration
    /// </summary>
    public static IServiceCollection ConfigureSecurityFromConfiguration(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "MethodCache:Security")
    {
        services.Configure<SecurityOptions>(configuration.GetSection(sectionName));
        return services;
    }

    /// <summary>
    /// Configures correlation options from configuration
    /// </summary>
    public static IServiceCollection ConfigureCorrelationFromConfiguration(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "MethodCache:Correlation")
    {
        services.Configure<AdvancedCorrelationOptions>(configuration.GetSection(sectionName));
        return services;
    }

    private static void DecorateService<TService>(
        IServiceCollection services,
        Func<TService, IServiceProvider, TService> decorator)
        where TService : class
    {
        var serviceDescriptor = services.FirstOrDefault(s => s.ServiceType == typeof(TService));
        if (serviceDescriptor != null)
        {
            services.Remove(serviceDescriptor);

            services.Add(new ServiceDescriptor(
                typeof(TService),
                provider =>
                {
                    TService originalService;
                    if (serviceDescriptor.ImplementationFactory != null)
                    {
                        originalService = (TService)serviceDescriptor.ImplementationFactory(provider);
                    }
                    else if (serviceDescriptor.ImplementationType != null)
                    {
                        originalService = (TService)ActivatorUtilities.CreateInstance(provider, serviceDescriptor.ImplementationType);
                    }
                    else
                    {
                        originalService = (TService)serviceDescriptor.ImplementationInstance!;
                    }

                    return decorator(originalService, provider);
                },
                serviceDescriptor.Lifetime));
        }
    }
}

/// <summary>
/// Configuration options for the metrics exporter factory
/// </summary>
public class CacheMetricsExporterFactoryOptions
{
    private readonly CacheMetricsExporterFactory _factory = new();

    public void RegisterExporter(string name, Func<IServiceProvider, ICacheMetricsExporter> factory)
    {
        _factory.RegisterExporter(name, options => factory(null!));
    }

    internal CacheMetricsExporterFactory GetFactory() => _factory;
}

/// <summary>
/// Hosted service for managing hot reload lifecycle
/// </summary>
public class HotReloadHostedService : IHostedService
{
    private readonly IConfigurationReloadManager _reloadManager;

    public HotReloadHostedService(IConfigurationReloadManager reloadManager)
    {
        _reloadManager = reloadManager;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Initialize hot reload monitoring
        await _reloadManager.ReloadFromSourceAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Cleanup handled by disposal
        return Task.CompletedTask;
    }
}