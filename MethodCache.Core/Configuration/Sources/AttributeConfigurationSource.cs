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
            if (etagAttributeType == null)
            {
                return;
            }

            var etagAttribute = method.GetCustomAttribute(etagAttributeType);
            if (etagAttribute == null)
            {
                return;
            }

            var metadata = new ETagMetadata
            {
                Strategy = etagAttributeType.GetProperty("Strategy")?.GetValue(etagAttribute)?.ToString(),
                IncludeParametersInETag = GetNullableValue<bool>(etagAttributeType, etagAttribute, "IncludeParametersInETag"),
                ETagGeneratorType = etagAttributeType.GetProperty("ETagGeneratorType")?.GetValue(etagAttribute) as Type,
                Metadata = etagAttributeType.GetProperty("Metadata")?.GetValue(etagAttribute) as string[],
                UseWeakETag = GetNullableValue<bool>(etagAttributeType, etagAttribute, "UseWeakETag")
            };

            var cacheDurationMinutes = GetNullableValue<int>(etagAttributeType, etagAttribute, "CacheDurationMinutes");
            if (cacheDurationMinutes.HasValue)
            {
                metadata.CacheDuration = TimeSpan.FromMinutes(cacheDurationMinutes.Value);
            }

            settings.SetETagMetadata(metadata);
        }

        private static T? GetNullableValue<T>(Type attributeType, object attribute, string propertyName) where T : struct
        {
            var property = attributeType.GetProperty(propertyName);
            if (property == null)
            {
                return null;
            }

            var value = property.GetValue(attribute);
            if (value == null)
            {
                return null;
            }

            if (value is T typed)
            {
                return typed;
            }

            // Handle nullable value types
            if (value.GetType() == typeof(T?))
            {
                var nullable = (T?)value;
                return nullable.HasValue ? nullable.Value : null;
            }

            return null;
        }
    }
}
