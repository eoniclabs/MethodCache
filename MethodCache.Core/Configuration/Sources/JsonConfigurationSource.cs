using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace MethodCache.Core.Configuration.Sources
{
    /// <summary>
    /// Configuration source that reads from JSON configuration files
    /// </summary>
    public class JsonConfigurationSource : IConfigurationSource
    {
        private readonly IConfiguration _configuration;
        private readonly string _sectionName;
        
        public JsonConfigurationSource(IConfiguration configuration, string sectionName = "MethodCache")
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _sectionName = sectionName;
        }
        
        public int Priority => 20; // Higher than attributes, lower than programmatic
        
        public bool SupportsRuntimeUpdates => true;
        
        public Task<IEnumerable<MethodCacheConfigEntry>> LoadAsync()
        {
            var entries = new List<MethodCacheConfigEntry>();
            var section = _configuration.GetSection(_sectionName);
            
            if (!section.Exists())
                return Task.FromResult(entries.AsEnumerable());
            
            // Load default settings
            var defaultsSection = section.GetSection("Defaults");
            var defaultSettings = LoadSettings(defaultsSection);
            
            // Load service-specific settings
            var servicesSection = section.GetSection("Services");
            foreach (var serviceConfig in servicesSection.GetChildren())
            {
                var methodKey = serviceConfig.Key; // Format: "IUserService.GetUser"
                var parts = methodKey.Split('.');
                
                if (parts.Length >= 2)
                {
                    var serviceName = string.Join(".", parts.Take(parts.Length - 1));
                    var methodName = parts.Last();
                    
                    var settings = LoadSettings(serviceConfig);
                    
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
            
            return Task.FromResult(entries.AsEnumerable());
        }
        
        private CacheMethodSettings LoadSettings(IConfigurationSection section)
        {
            var settings = new CacheMethodSettings();
            
            var durationStr = section["Duration"];
            if (!string.IsNullOrEmpty(durationStr))
            {
                settings.Duration = TimeSpan.Parse(durationStr);
            }
            
            var tags = section.GetSection("Tags").Get<string[]>();
            if (tags != null)
            {
                settings.Tags = tags.ToList();
            }
            
            var version = section.GetValue<int?>("Version");
            if (version.HasValue)
            {
                settings.Version = version.Value;
            }
            
            // Load ETag settings
            var etagSection = section.GetSection("ETag");
            if (etagSection.Exists())
            {
                settings.ETag = new ETagSettings
                {
                    Strategy = etagSection.GetValue("Strategy", ETagGenerationStrategy.ContentHash),
                    IncludeParametersInETag = etagSection.GetValue("IncludeParametersInETag", true),
                    UseWeakETag = etagSection.GetValue("UseWeakETag", false),
                    Metadata = etagSection.GetSection("Metadata").Get<string[]>()
                };
                
                var etagDuration = etagSection["CacheDuration"];
                if (!string.IsNullOrEmpty(etagDuration))
                {
                    settings.ETag.CacheDuration = TimeSpan.Parse(etagDuration);
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
    /// JSON configuration model for serialization
    /// </summary>
    public class MethodCacheJsonConfiguration
    {
        public DefaultsConfiguration? Defaults { get; set; }
        public Dictionary<string, ServiceMethodConfiguration>? Services { get; set; }
    }
    
    public class DefaultsConfiguration
    {
        public string? Duration { get; set; }
        public string[]? Tags { get; set; }
        public int? Version { get; set; }
        public ETagConfiguration? ETag { get; set; }
    }
    
    public class ServiceMethodConfiguration
    {
        public string? Duration { get; set; }
        public string[]? Tags { get; set; }
        public int? Version { get; set; }
        public ETagConfiguration? ETag { get; set; }
    }
    
    public class ETagConfiguration
    {
        public string? Strategy { get; set; }
        public bool? IncludeParametersInETag { get; set; }
        public bool? UseWeakETag { get; set; }
        public string[]? Metadata { get; set; }
        public string? CacheDuration { get; set; }
    }
}