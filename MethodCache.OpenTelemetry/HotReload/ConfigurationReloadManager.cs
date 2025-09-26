using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.OpenTelemetry.Configuration;
using MethodCache.OpenTelemetry.Correlation;
using MethodCache.OpenTelemetry.Security;

namespace MethodCache.OpenTelemetry.HotReload;

/// <summary>
/// Implementation of configuration reload manager with hot reload support
/// </summary>
public class ConfigurationReloadManager : IConfigurationReloadManager, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigurationReloadManager> _logger;
    private readonly ConcurrentDictionary<ConfigurationSection, object> _currentConfigurations = new();
    private readonly ConcurrentDictionary<ConfigurationSection, List<ConfigurationHistoryEntry>> _configurationHistory = new();
    private readonly ConcurrentDictionary<ConfigurationSection, IConfigurationChangeHandler<object>> _changeHandlers = new();
    private readonly ConcurrentDictionary<ConfigurationSection, bool> _hotReloadEnabled = new();
    private readonly SemaphoreSlim _updateSemaphore = new(1, 1);
    private readonly FileSystemWatcher? _fileWatcher;
    private volatile bool _disposed;

    public event EventHandler<ConfigurationChangedEventArgs>? ConfigurationChanged;

    public ConfigurationReloadManager(
        IConfiguration configuration,
        ILogger<ConfigurationReloadManager> logger,
        string? configurationPath = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        InitializeConfigurations();
        RegisterDefaultHandlers();

        // Set up file system watcher if configuration path is provided
        if (!string.IsNullOrEmpty(configurationPath) && File.Exists(configurationPath))
        {
            try
            {
                var directory = Path.GetDirectoryName(configurationPath);
                var fileName = Path.GetFileName(configurationPath);

                if (!string.IsNullOrEmpty(directory) && !string.IsNullOrEmpty(fileName))
                {
                    _fileWatcher = new FileSystemWatcher(directory, fileName)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                        EnableRaisingEvents = true
                    };

                    _fileWatcher.Changed += OnConfigurationFileChanged;
                    _logger.LogInformation("File system watcher configured for {ConfigPath}", configurationPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set up file system watcher for configuration file");
            }
        }
    }

    public T GetConfiguration<T>(ConfigurationSection section) where T : class
    {
        if (_currentConfigurations.TryGetValue(section, out var config) && config is T typedConfig)
        {
            return typedConfig;
        }

        throw new InvalidOperationException($"Configuration for section {section} not found or has wrong type");
    }

    public async Task<bool> UpdateConfigurationAsync<T>(ConfigurationSection section, T newConfiguration, CancellationToken cancellationToken = default) where T : class
    {
        if (_disposed) return false;

        await _updateSemaphore.WaitAsync(cancellationToken);
        try
        {
            // Validate configuration
            if (_changeHandlers.TryGetValue(section, out var handler))
            {
                var validationResult = await ValidateConfigurationInternalAsync(handler, newConfiguration);
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning("Configuration validation failed for section {Section}: {Errors}",
                        section, string.Join(", ", validationResult.Errors));
                    return false;
                }
            }

            var oldConfiguration = _currentConfigurations.TryGetValue(section, out var old) ? old : null;

            // Store history
            StoreConfigurationHistory(section, newConfiguration);

            // Update current configuration
            _currentConfigurations[section] = newConfiguration;

            // Handle configuration change
            if (handler != null && _hotReloadEnabled.GetValueOrDefault(section, true))
            {
                try
                {
                    await ((IConfigurationChangeHandler<T>)handler).HandleConfigurationChangeAsync(
                        (T?)oldConfiguration, newConfiguration, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling configuration change for section {Section}", section);

                    // Rollback on error
                    if (oldConfiguration != null)
                    {
                        _currentConfigurations[section] = oldConfiguration;
                    }
                    return false;
                }
            }

            // Fire event
            var eventArgs = new ConfigurationChangedEventArgs
            {
                Section = section,
                PropertyName = "All",
                OldValue = oldConfiguration,
                NewValue = newConfiguration
            };

            ConfigurationChanged?.Invoke(this, eventArgs);

            _logger.LogInformation("Configuration updated successfully for section {Section}", section);
            return true;
        }
        finally
        {
            _updateSemaphore.Release();
        }
    }

    public async Task<bool> UpdatePropertyAsync(ConfigurationSection section, string propertyName, object? value, CancellationToken cancellationToken = default)
    {
        if (_disposed) return false;

        await _updateSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (!_currentConfigurations.TryGetValue(section, out var currentConfig))
            {
                _logger.LogWarning("Configuration section {Section} not found", section);
                return false;
            }

            // Create a copy of the current configuration
            var configCopy = DeepClone(currentConfig);
            var oldValue = GetPropertyValue(configCopy, propertyName);

            // Update the property
            if (!SetPropertyValue(configCopy, propertyName, value))
            {
                _logger.LogWarning("Failed to set property {PropertyName} in section {Section}", propertyName, section);
                return false;
            }

            // Validate the updated configuration
            if (_changeHandlers.TryGetValue(section, out var handler))
            {
                var validationResult = await ValidateConfigurationInternalAsync(handler, configCopy);
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning("Property update validation failed for {Section}.{Property}: {Errors}",
                        section, propertyName, string.Join(", ", validationResult.Errors));
                    return false;
                }
            }

            // Store history
            StoreConfigurationHistory(section, configCopy, propertyName, oldValue, value);

            // Update current configuration
            _currentConfigurations[section] = configCopy;

            // Handle configuration change
            if (handler != null && _hotReloadEnabled.GetValueOrDefault(section, true))
            {
                try
                {
                    await HandleConfigurationChangeAsync(handler, currentConfig, configCopy, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling property change for {Section}.{Property}", section, propertyName);

                    // Rollback on error
                    _currentConfigurations[section] = currentConfig;
                    return false;
                }
            }

            // Fire event
            var eventArgs = new ConfigurationChangedEventArgs
            {
                Section = section,
                PropertyName = propertyName,
                OldValue = oldValue,
                NewValue = value
            };

            ConfigurationChanged?.Invoke(this, eventArgs);

            _logger.LogInformation("Property {PropertyName} updated successfully in section {Section}", propertyName, section);
            return true;
        }
        finally
        {
            _updateSemaphore.Release();
        }
    }

    public async Task<ValidationResult> ValidateConfigurationAsync<T>(ConfigurationSection section, T configuration) where T : class
    {
        if (_changeHandlers.TryGetValue(section, out var handler))
        {
            return await ValidateConfigurationInternalAsync(handler, configuration);
        }

        return new ValidationResult { IsValid = true };
    }

    public IEnumerable<ConfigurationHistoryEntry> GetConfigurationHistory(ConfigurationSection section)
    {
        return _configurationHistory.GetValueOrDefault(section, new List<ConfigurationHistoryEntry>());
    }

    public async Task<bool> RollbackConfigurationAsync(ConfigurationSection section, DateTime timestamp, CancellationToken cancellationToken = default)
    {
        var history = GetConfigurationHistory(section).ToList();
        var targetEntry = history.FirstOrDefault(h => h.Timestamp <= timestamp);

        if (targetEntry == null)
        {
            _logger.LogWarning("No configuration history found for rollback to {Timestamp} in section {Section}", timestamp, section);
            return false;
        }

        _logger.LogInformation("Rolling back configuration for section {Section} to {Timestamp}", section, targetEntry.Timestamp);
        return await UpdateConfigurationAsync(section, targetEntry.Configuration, cancellationToken);
    }

    public Dictionary<ConfigurationSection, object> GetAllConfigurations()
    {
        return new Dictionary<ConfigurationSection, object>(_currentConfigurations);
    }

    public async Task ReloadFromSourceAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Reloading configuration from source");

        try
        {
            // Trigger configuration reload
            if (_configuration is IConfigurationRoot configRoot)
            {
                configRoot.Reload();
            }

            // Reinitialize configurations
            InitializeConfigurations();

            await Task.CompletedTask;
            _logger.LogInformation("Configuration reloaded successfully from source");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reloading configuration from source");
            throw;
        }
    }

    public void SetHotReloadEnabled(ConfigurationSection section, bool enabled)
    {
        _hotReloadEnabled[section] = enabled;
        _logger.LogInformation("Hot reload {Status} for section {Section}", enabled ? "enabled" : "disabled", section);
    }

    private void InitializeConfigurations()
    {
        // Initialize OpenTelemetry configuration
        var otelOptions = new OpenTelemetryOptions();
        _configuration.GetSection("MethodCache:OpenTelemetry").Bind(otelOptions);
        _currentConfigurations[ConfigurationSection.OpenTelemetry] = otelOptions;

        // Initialize Security configuration
        var securityOptions = new SecurityOptions();
        _configuration.GetSection("MethodCache:Security").Bind(securityOptions);
        _currentConfigurations[ConfigurationSection.Security] = securityOptions;

        // Initialize Correlation configuration
        var correlationOptions = new AdvancedCorrelationOptions();
        _configuration.GetSection("MethodCache:Correlation").Bind(correlationOptions);
        _currentConfigurations[ConfigurationSection.Correlation] = correlationOptions;

        // Enable hot reload by default for all sections
        foreach (ConfigurationSection section in Enum.GetValues<ConfigurationSection>())
        {
            _hotReloadEnabled.TryAdd(section, true);
        }
    }

    private void RegisterDefaultHandlers()
    {
        RegisterHandler(new OpenTelemetryOptionsChangeHandler());
        RegisterHandler(new SecurityOptionsChangeHandler());
    }

    private void RegisterHandler<T>(IConfigurationChangeHandler<T> handler) where T : class
    {
        _changeHandlers[handler.Section] = (IConfigurationChangeHandler<object>)handler;
    }

    private void OnConfigurationFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_disposed) return;

        _logger.LogDebug("Configuration file changed: {FileName}", e.Name);

        // Debounce file changes
        Task.Delay(500).ContinueWith(async _ =>
        {
            try
            {
                await ReloadFromSourceAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reloading configuration after file change");
            }
        });
    }

    private void StoreConfigurationHistory(ConfigurationSection section, object configuration, string? propertyName = null, object? oldValue = null, object? newValue = null)
    {
        var historyList = _configurationHistory.GetOrAdd(section, _ => new List<ConfigurationHistoryEntry>());

        var entry = new ConfigurationHistoryEntry
        {
            Timestamp = DateTime.UtcNow,
            Configuration = DeepClone(configuration),
            ChangedBy = "HotReload", // Could be enhanced to track actual user
            ChangeReason = propertyName != null ? $"Property {propertyName} changed" : "Full configuration update"
        };

        if (propertyName != null)
        {
            entry.Changes[propertyName] = new { Old = oldValue, New = newValue };
        }

        lock (historyList)
        {
            historyList.Add(entry);

            // Keep only last 50 entries
            if (historyList.Count > 50)
            {
                historyList.RemoveAt(0);
            }
        }
    }

    private async Task<ValidationResult> ValidateConfigurationInternalAsync<T>(IConfigurationChangeHandler<object> handler, T configuration) where T : class
    {
        if (handler is IConfigurationChangeHandler<T> typedHandler)
        {
            return await typedHandler.ValidateAsync(configuration);
        }

        return new ValidationResult { IsValid = true };
    }

    private async Task HandleConfigurationChangeAsync(IConfigurationChangeHandler<object> handler, object oldConfig, object newConfig, CancellationToken cancellationToken)
    {
        await handler.HandleConfigurationChangeAsync(oldConfig, newConfig, cancellationToken);
    }

    private static object DeepClone(object obj)
    {
        // Simple JSON-based deep clone
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = false });
        return JsonSerializer.Deserialize(json, obj.GetType()) ?? obj;
    }

    private static object? GetPropertyValue(object obj, string propertyName)
    {
        var property = obj.GetType().GetProperty(propertyName);
        return property?.GetValue(obj);
    }

    private static bool SetPropertyValue(object obj, string propertyName, object? value)
    {
        var property = obj.GetType().GetProperty(propertyName);
        if (property == null || !property.CanWrite)
            return false;

        try
        {
            property.SetValue(obj, value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _fileWatcher?.Dispose();
        _updateSemaphore.Dispose();

        _logger.LogInformation("Configuration reload manager disposed");
    }
}