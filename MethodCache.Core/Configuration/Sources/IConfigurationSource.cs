using System.Collections.Generic;
using System.Threading.Tasks;

namespace MethodCache.Core.Configuration.Sources
{
    /// <summary>
    /// Represents a source of cache configuration that can be loaded and applied
    /// </summary>
    public interface IConfigurationSource
    {
        /// <summary>
        /// Load configuration settings from the source
        /// </summary>
        Task<IEnumerable<MethodCacheConfigEntry>> LoadAsync();
        
        /// <summary>
        /// Gets the priority of this configuration source (higher priority overrides lower)
        /// </summary>
        int Priority { get; }
        
        /// <summary>
        /// Gets whether this source supports runtime updates
        /// </summary>
        bool SupportsRuntimeUpdates { get; }
    }
    
    /// <summary>
    /// Represents a single cache configuration entry from a configuration source
    /// </summary>
    public class MethodCacheConfigEntry
    {
        public string ServiceType { get; set; } = string.Empty;
        public string MethodName { get; set; } = string.Empty;
        public CacheMethodSettings Settings { get; set; } = new();
        
        /// <summary>
        /// Gets the fully qualified method key
        /// </summary>
        public string MethodKey => string.IsNullOrEmpty(ServiceType) || string.IsNullOrEmpty(MethodName) 
            ? string.Empty 
            : $"{ServiceType}.{MethodName}";
    }
}