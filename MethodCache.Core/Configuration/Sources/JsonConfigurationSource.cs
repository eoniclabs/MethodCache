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

            ApplyETagSettings(section.GetSection("ETag"), settings);
            
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

            var defaultEtag = defaults.GetETagMetadata();
            if (defaultEtag != null)
            {
                settings.MergeWithDefaultETagMetadata(defaultEtag);
            }
        }

        private static void ApplyETagSettings(IConfigurationSection etagSection, CacheMethodSettings settings)
        {
            if (etagSection == null || !etagSection.Exists())
            {
                return;
            }

            var metadata = new ETagMetadata
            {
                Strategy = etagSection.GetValue<string>("Strategy"),
                IncludeParametersInETag = etagSection.GetValue<bool?>("IncludeParametersInETag"),
                UseWeakETag = etagSection.GetValue<bool?>("UseWeakETag"),
                Metadata = etagSection.GetSection("Metadata").Get<string[]>(),
                CacheDuration = TryParseTimeSpan(etagSection["CacheDuration"]),
                ETagGeneratorType = ParseType(etagSection.GetValue<string>("ETagGeneratorType"))
            };

            settings.SetETagMetadata(metadata);
        }

        private static TimeSpan? TryParseTimeSpan(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return TimeSpan.TryParse(value, out var duration) ? duration : null;
        }

        private static Type? ParseType(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return Type.GetType(value, throwOnError: false);
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
    }
    
    public class ServiceMethodConfiguration
    {
        public string? Duration { get; set; }
        public string[]? Tags { get; set; }
        public int? Version { get; set; }
    }
}
