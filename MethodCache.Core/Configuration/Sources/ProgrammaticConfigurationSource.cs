using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MethodCache.Core.Configuration.Sources
{
    /// <summary>
    /// Configuration source for programmatic/code-based configuration
    /// </summary>
    public class ProgrammaticConfigurationSource : IConfigurationSource
    {
        private readonly Dictionary<string, CacheMethodSettings> _configurations = new();
        
        public int Priority => 30; // High priority - but runtime management can override
        
        public bool SupportsRuntimeUpdates => false;
        
        public void AddMethodConfiguration(string serviceType, string methodName, CacheMethodSettings settings)
        {
            var key = $"{serviceType}.{methodName}";
            _configurations[key] = settings;
        }
        
        public Task<IEnumerable<MethodCacheConfigEntry>> LoadAsync()
        {
            var entries = new List<MethodCacheConfigEntry>();
            
            foreach (var kvp in _configurations)
            {
                var parts = kvp.Key.Split('.');
                if (parts.Length >= 2)
                {
                    var serviceName = string.Join(".", parts.Take(parts.Length - 1));
                    var methodName = parts.Last();
                    
                    entries.Add(new MethodCacheConfigEntry
                    {
                        ServiceType = serviceName,
                        MethodName = methodName,
                        Settings = kvp.Value
                    });
                }
            }
            
            return Task.FromResult(entries.AsEnumerable());
        }
    }
}