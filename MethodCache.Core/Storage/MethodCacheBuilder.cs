using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core.Configuration;

namespace MethodCache.Core.Storage;

/// <summary>
/// Internal implementation of the MethodCache builder.
/// </summary>
internal class MethodCacheBuilder : IMethodCacheBuilder
{
    private IL1Provider? _l1Provider;
    private IL2Provider? _l2Provider;
    private IL3Provider? _l3Provider;
    private readonly ILogger<MethodCacheBuilder>? _logger;

    public IServiceCollection Services { get; }

    public MethodCacheBuilder(IServiceCollection services)
    {
        Services = services;

        // Try to get logger if available
        using var tempProvider = services.BuildServiceProvider();
        _logger = tempProvider.GetService<ILogger<MethodCacheBuilder>>();
    }

    public IMethodCacheBuilder WithL1(IL1Provider provider)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));

        _l1Provider = provider;
        provider.ValidateConfiguration();
        provider.Register(Services);

        UpdateHybridRegistration();
        _logger?.LogDebug("Configured L1 provider: {ProviderName}", provider.Name);

        return this;
    }

    public IMethodCacheBuilder WithL2(IL2Provider provider)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));

        _l2Provider = provider;
        provider.ValidateConfiguration();
        provider.Register(Services);

        UpdateHybridRegistration();
        _logger?.LogDebug("Configured L2 provider: {ProviderName}", provider.Name);

        return this;
    }

    public IMethodCacheBuilder WithL3(IL3Provider provider)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));

        _l3Provider = provider;
        provider.ValidateConfiguration();
        provider.Register(Services);

        UpdateHybridRegistration();
        _logger?.LogDebug("Configured L3 provider: {ProviderName}", provider.Name);

        return this;
    }

    public IMethodCacheBuilder FromConfiguration(IConfiguration configuration, string sectionName = "MethodCache")
    {
        if (configuration == null) throw new ArgumentNullException(nameof(configuration));

        var config = configuration.GetSection(sectionName).Get<MethodCacheConfiguration>();
        if (config == null)
        {
            _logger?.LogWarning("No MethodCache configuration found in section '{SectionName}'", sectionName);
            return this;
        }

        // Configure L1 if enabled
        if (config.L1?.Enabled == true)
        {
            // For now, only memory provider is available in Core
            // This will be extended by provider packages
            _logger?.LogInformation("L1 cache enabled from configuration");
        }

        // L2 and L3 configuration will be handled by their respective provider packages
        // through extension methods that check the configuration

        return this;
    }

    public IServiceCollection Build()
    {
        ValidateConfiguration();

        var configuredLayers = new List<string>();
        if (_l1Provider != null) configuredLayers.Add($"L1({_l1Provider.Name})");
        if (_l2Provider != null) configuredLayers.Add($"L2({_l2Provider.Name})");
        if (_l3Provider != null) configuredLayers.Add($"L3({_l3Provider.Name})");

        _logger?.LogInformation("MethodCache configured with layers: {Layers}", string.Join(" + ", configuredLayers));

        return Services;
    }

    private void ValidateConfiguration()
    {
        if (_l1Provider == null)
            throw new InvalidOperationException("L1 cache is required. Call WithL1() or use AddMethodCache() with default memory provider.");

        if (_l2Provider == null && _l3Provider == null)
            _logger?.LogWarning("Only L1 cache configured. Consider adding L2 or L3 for better performance and persistence.");
    }

    private void UpdateHybridRegistration()
    {
        // Remove previous HybridStorageManager registration
        Services.RemoveAll<HybridStorageManager>();
        Services.RemoveAll<IStorageProvider>();

        // Register HybridStorageManager with current configuration
        Services.TryAddSingleton<HybridStorageManager>(provider =>
        {
            var memoryStorage = _l1Provider != null ? provider.GetService<IMemoryStorage>() : null;
            var options = provider.GetRequiredService<IOptions<StorageOptions>>();
            var logger = provider.GetRequiredService<ILogger<HybridStorageManager>>();
            var metricsProvider = provider.GetService<ICacheMetricsProvider>();

            // Get L2 storage if configured
            IStorageProvider? l2Storage = null;
            if (_l2Provider != null)
            {
                // L2 providers register themselves as IStorageProvider with a specific name/key
                // This is a simplified approach - in practice, we'd need a registry pattern
                l2Storage = provider.GetServices<IStorageProvider>()
                                   .FirstOrDefault(sp => sp.Name.Contains(_l2Provider.Name));
            }

            // Get L3 storage if configured
            IPersistentStorageProvider? l3Storage = null;
            if (_l3Provider != null)
            {
                l3Storage = provider.GetService<IPersistentStorageProvider>();
            }

            // Get backplane if available
            var backplane = provider.GetService<IBackplane>();

            return new HybridStorageManager(memoryStorage!, options, logger, l2Storage, l3Storage, backplane, metricsProvider);
        });

        // Register as IStorageProvider for compatibility
        Services.TryAddSingleton<IStorageProvider>(provider => provider.GetRequiredService<HybridStorageManager>());
    }
}
