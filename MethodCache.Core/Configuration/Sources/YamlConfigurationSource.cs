using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MethodCache.Core.Configuration.Sources
{
    /// <summary>
    /// Configuration source that reads from YAML configuration files
    /// </summary>
    public class YamlConfigurationSource : IConfigurationSource
    {
        private readonly string _yamlFilePath;
        private readonly IDeserializer _deserializer;
        
        public YamlConfigurationSource(string yamlFilePath)
        {
            _yamlFilePath = yamlFilePath ?? throw new ArgumentNullException(nameof(yamlFilePath));
            _deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
        }
        
        public int Priority => 20; // Same as JSON
        
        public bool SupportsRuntimeUpdates => true;
        
        public async Task<IEnumerable<MethodCacheConfigEntry>> LoadAsync()
        {
            var entries = new List<MethodCacheConfigEntry>();
            
            if (!File.Exists(_yamlFilePath))
                return entries;
            
            var yamlContent = await File.ReadAllTextAsync(_yamlFilePath);
            var config = _deserializer.Deserialize<MethodCacheYamlConfiguration>(yamlContent);
            
            if (config == null)
                return entries;
            
            // Load default settings
            var defaultSettings = config.Defaults != null 
                ? ConvertToSettings(config.Defaults) 
                : new CacheMethodSettings();
            
            // Load service-specific settings
            if (config.Services != null)
            {
                foreach (var serviceConfig in config.Services)
                {
                    var methodKey = serviceConfig.Key; // Format: "IUserService.GetUser"
                    var parts = methodKey.Split('.');
                    
                    if (parts.Length >= 2)
                    {
                        var serviceName = string.Join(".", parts.Take(parts.Length - 1));
                        var methodName = parts.Last();
                        
                        var settings = ConvertToSettings(serviceConfig.Value);
                        
                        // Apply defaults if not specified
                        ApplyDefaults(settings, defaultSettings);
                        
                        entries.Add(new MethodCacheConfigEntry
                        {
                            ServiceType = serviceName,
                            MethodName = methodName,
                            Settings = settings
                        });
                    }
                }
            }
            
            return entries;
        }
        
        private CacheMethodSettings ConvertToSettings(YamlMethodConfiguration config)
        {
            var settings = new CacheMethodSettings();
            
            if (!string.IsNullOrEmpty(config.Duration))
            {
                settings.Duration = TimeSpan.Parse(config.Duration);
            }
            
            if (config.Tags != null)
            {
                settings.Tags = config.Tags.ToList();
            }
            
            if (config.Version.HasValue)
            {
                settings.Version = config.Version.Value;
            }
            
            if (config.ETag != null)
            {
                settings.ETag = new ETagSettings
                {
                    Strategy = Enum.TryParse<ETagGenerationStrategy>(config.ETag.Strategy, out var strategy) 
                        ? strategy 
                        : ETagGenerationStrategy.ContentHash,
                    IncludeParametersInETag = config.ETag.IncludeParametersInETag ?? true,
                    UseWeakETag = config.ETag.UseWeakETag ?? false,
                    Metadata = config.ETag.Metadata
                };
                
                if (!string.IsNullOrEmpty(config.ETag.CacheDuration))
                {
                    settings.ETag.CacheDuration = TimeSpan.Parse(config.ETag.CacheDuration);
                }
            }
            
            return settings;
        }
        
        private void ApplyDefaults(CacheMethodSettings settings, CacheMethodSettings defaults)
        {
            settings.Duration ??= defaults.Duration;
            settings.Version ??= defaults.Version;
            
            if (settings.Tags.Count == 0 && defaults.Tags.Count > 0)
            {
                settings.Tags = new List<string>(defaults.Tags);
            }
            
            if (settings.ETag == null && defaults.ETag != null)
            {
                settings.ETag = new ETagSettings
                {
                    Strategy = defaults.ETag.Strategy,
                    IncludeParametersInETag = defaults.ETag.IncludeParametersInETag,
                    UseWeakETag = defaults.ETag.UseWeakETag,
                    Metadata = defaults.ETag.Metadata,
                    CacheDuration = defaults.ETag.CacheDuration
                };
            }
        }
    }
    
    /// <summary>
    /// YAML configuration model
    /// </summary>
    public class MethodCacheYamlConfiguration
    {
        public YamlMethodConfiguration? Defaults { get; set; }
        public Dictionary<string, YamlMethodConfiguration>? Services { get; set; }
    }
    
    public class YamlMethodConfiguration
    {
        public string? Duration { get; set; }
        public string[]? Tags { get; set; }
        public int? Version { get; set; }
        public YamlETagConfiguration? ETag { get; set; }
    }
    
    public class YamlETagConfiguration
    {
        public string? Strategy { get; set; }
        public bool? IncludeParametersInETag { get; set; }
        public bool? UseWeakETag { get; set; }
        public string[]? Metadata { get; set; }
        public string? CacheDuration { get; set; }
    }
}