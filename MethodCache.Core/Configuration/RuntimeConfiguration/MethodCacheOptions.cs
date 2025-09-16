using System;
using System.Collections.Generic;

namespace MethodCache.Core.Configuration.RuntimeConfiguration
{
    /// <summary>
    /// Options for MethodCache that can be configured via IOptions pattern
    /// </summary>
    public class MethodCacheOptions
    {
        /// <summary>
        /// Default cache duration for all methods
        /// </summary>
        public TimeSpan DefaultDuration { get; set; } = TimeSpan.FromMinutes(15);
        
        /// <summary>
        /// Global tags applied to all cached methods
        /// </summary>
        public List<string> GlobalTags { get; set; } = new();
        
        /// <summary>
        /// Whether to enable debug logging
        /// </summary>
        public bool EnableDebugLogging { get; set; } = false;
        
        /// <summary>
        /// Whether to enable metrics collection
        /// </summary>
        public bool EnableMetrics { get; set; } = true;
        
        /// <summary>
        /// Service-specific configurations
        /// </summary>
        public Dictionary<string, ServiceCacheOptions> Services { get; set; } = new();
        
        /// <summary>
        /// Global ETag settings
        /// </summary>
        public ETagOptions? ETag { get; set; }
    }
    
    /// <summary>
    /// Service-specific cache options
    /// </summary>
    public class ServiceCacheOptions
    {
        /// <summary>
        /// Method-specific configurations (key is method name)
        /// </summary>
        public Dictionary<string, MethodOptions> Methods { get; set; } = new();
        
        /// <summary>
        /// Default duration for all methods in this service
        /// </summary>
        public TimeSpan? DefaultDuration { get; set; }
        
        /// <summary>
        /// Default tags for all methods in this service
        /// </summary>
        public List<string> DefaultTags { get; set; } = new();
    }
    
    /// <summary>
    /// Method-specific cache options
    /// </summary>
    public class MethodOptions
    {
        public TimeSpan? Duration { get; set; }
        public List<string> Tags { get; set; } = new();
        public int? Version { get; set; }
        public bool? Enabled { get; set; }
        public ETagOptions? ETag { get; set; }
    }
    
    /// <summary>
    /// ETag-specific options
    /// </summary>
    public class ETagOptions
    {
        public string Strategy { get; set; } = "ContentHash";
        public bool IncludeParametersInETag { get; set; } = true;
        public bool UseWeakETag { get; set; } = false;
        public List<string> Metadata { get; set; } = new();
        public TimeSpan? CacheDuration { get; set; }
    }
}