using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace MethodCache.Core.Configuration.Sources
{
    /// <summary>
    /// Configuration source that reads from Cache attributes on methods
    /// </summary>
    public class AttributeConfigurationSource : IConfigurationSource
    {
        private readonly Assembly[] _assemblies;
        
        public AttributeConfigurationSource(params Assembly[] assemblies)
        {
            _assemblies = assemblies ?? new[] { Assembly.GetCallingAssembly() };
        }
        
        public int Priority => 10; // Lowest priority - attributes are defaults
        
        public bool SupportsRuntimeUpdates => false;
        
        public Task<IEnumerable<MethodCacheConfigEntry>> LoadAsync()
        {
            var entries = new List<MethodCacheConfigEntry>();
            
            foreach (var assembly in _assemblies)
            {
                try
                {
                    var types = assembly.GetTypes()
                        .Where(t => t.IsInterface || t.IsClass);
                    
                    foreach (var type in types)
                    {
                        var methods = type.GetMethods()
                            .Where(m => m.GetCustomAttribute<CacheAttribute>() != null);
                        
                        foreach (var method in methods)
                        {
                            var cacheAttribute = method.GetCustomAttribute<CacheAttribute>();
                            if (cacheAttribute != null)
                            {
                                var entry = new MethodCacheConfigEntry
                                {
                                    ServiceType = type.FullName ?? type.Name,
                                    MethodName = method.Name,
                                    Settings = new CacheMethodSettings
                                    {
                                        Duration = string.IsNullOrEmpty(cacheAttribute.Duration)
                                            ? TimeSpan.FromMinutes(15)
                                            : TimeSpan.Parse(cacheAttribute.Duration),
                                        Tags = cacheAttribute.Tags?.ToList() ?? new List<string>()
                                    }
                                };
                                
                                // Check for ETag attribute
                                LoadETagSettings(method, entry.Settings);
                                
                                entries.Add(entry);
                            }
                        }
                    }
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // Handle partial load failures
                    var loadableTypes = ex.Types.Where(t => t != null).Cast<Type>();
                    // Process loadable types...
                }
            }
            
            return Task.FromResult(entries.AsEnumerable());
        }
        
        private void LoadETagSettings(MethodInfo method, CacheMethodSettings settings)
        {
            var etagAttributeType = Type.GetType("MethodCache.ETags.Attributes.ETagAttribute, MethodCache.ETags");
            if (etagAttributeType == null) return;
            
            var etagAttribute = method.GetCustomAttribute(etagAttributeType);
            if (etagAttribute != null)
            {
                settings.ETag = new ETagSettings
                {
                    Strategy = (ETagGenerationStrategy)(etagAttributeType.GetProperty("Strategy")?.GetValue(etagAttribute) ?? ETagGenerationStrategy.ContentHash),
                    IncludeParametersInETag = (bool)(etagAttributeType.GetProperty("IncludeParametersInETag")?.GetValue(etagAttribute) ?? true),
                    ETagGeneratorType = (Type?)etagAttributeType.GetProperty("ETagGeneratorType")?.GetValue(etagAttribute),
                    Metadata = (string[]?)etagAttributeType.GetProperty("Metadata")?.GetValue(etagAttribute),
                    UseWeakETag = (bool)(etagAttributeType.GetProperty("UseWeakETag")?.GetValue(etagAttribute) ?? false)
                };
                
                var cacheDurationMinutes = etagAttributeType.GetProperty("CacheDurationMinutes")?.GetValue(etagAttribute) as int?;
                if (cacheDurationMinutes.HasValue)
                {
                    settings.ETag.CacheDuration = TimeSpan.FromMinutes(cacheDurationMinutes.Value);
                }
            }
        }
    }
}