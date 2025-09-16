using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using MethodCache.Core.Configuration.Sources;

namespace MethodCache.Core.Configuration
{
    /// <summary>
    /// Manages multiple configuration sources and provides merged configuration
    /// </summary>
    public class ConfigurationManager : IMethodCacheConfigurationManager
    {
        private readonly List<IConfigurationSource> _sources = new();
        private readonly Dictionary<string, CacheMethodSettings> _mergedConfiguration = new();
        private readonly ILogger<ConfigurationManager>? _logger;
        private readonly ReaderWriterLockSlim _lock = new();
        
        public event EventHandler<ConfigurationChangedEventArgs>? ConfigurationChanged;
        
        public ConfigurationManager(ILogger<ConfigurationManager>? logger = null)
        {
            _logger = logger;
        }
        
        /// <summary>
        /// Adds a configuration source
        /// </summary>
        public void AddSource(IConfigurationSource source)
        {
            _sources.Add(source);
            _sources.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }
        
        /// <summary>
        /// Loads and merges configuration from all sources
        /// </summary>
        public async Task LoadConfigurationAsync()
        {
            var allEntries = new List<MethodCacheConfigEntry>();
            
            // Load from all sources in priority order
            foreach (var source in _sources.OrderBy(s => s.Priority))
            {
                try
                {
                    var entries = await source.LoadAsync();
                    allEntries.AddRange(entries);
                    
                    _logger?.LogDebug($"Loaded {entries.Count()} entries from {source.GetType().Name}");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, $"Failed to load configuration from {source.GetType().Name}");
                }
            }
            
            // Merge configurations (higher priority wins)
            _lock.EnterWriteLock();
            try
            {
                _mergedConfiguration.Clear();
                
                foreach (var entry in allEntries.OrderBy(e => GetSourcePriority(e)))
                {
                    if (!string.IsNullOrEmpty(entry.MethodKey))
                    {
                        _mergedConfiguration[entry.MethodKey] = entry.Settings;
                    }
                }
                
                _logger?.LogInformation($"Configuration loaded with {_mergedConfiguration.Count} method configurations");
            }
            finally
            {
                _lock.ExitWriteLock();
            }
            
            // Notify listeners
            ConfigurationChanged?.Invoke(this, new ConfigurationChangedEventArgs());
        }
        
        /// <summary>
        /// Gets configuration for a specific method
        /// </summary>
        public CacheMethodSettings? GetMethodConfiguration(string serviceType, string methodName)
        {
            var key = $"{serviceType}.{methodName}";
            
            _lock.EnterReadLock();
            try
            {
                return _mergedConfiguration.TryGetValue(key, out var settings) ? settings : null;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
        
        /// <summary>
        /// Gets all configurations
        /// </summary>
        public IReadOnlyDictionary<string, CacheMethodSettings> GetAllConfigurations()
        {
            _lock.EnterReadLock();
            try
            {
                return new Dictionary<string, CacheMethodSettings>(_mergedConfiguration);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
        
        private int GetSourcePriority(MethodCacheConfigEntry entry)
        {
            // This would track which source the entry came from
            // For now, return 0
            return 0;
        }
        
        public void Dispose()
        {
            _lock?.Dispose();
        }
    }
    
    /// <summary>
    /// Interface for the configuration manager
    /// </summary>
    public interface IMethodCacheConfigurationManager : IDisposable
    {
        void AddSource(IConfigurationSource source);
        Task LoadConfigurationAsync();
        CacheMethodSettings? GetMethodConfiguration(string serviceType, string methodName);
        IReadOnlyDictionary<string, CacheMethodSettings> GetAllConfigurations();
        event EventHandler<ConfigurationChangedEventArgs>? ConfigurationChanged;
    }
    
    public class ConfigurationChangedEventArgs : EventArgs
    {
        public DateTime Timestamp { get; } = DateTime.UtcNow;
    }
}