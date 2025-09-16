using System;
using System.Collections.Generic;

namespace MethodCache.Core.Configuration
{
    public class CacheMethodSettings
    {
        public TimeSpan? Duration { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public int? Version { get; set; }
        public Type? KeyGeneratorType { get; set; }
        public Func<CacheExecutionContext, bool>? Condition { get; set; }
        public Action<CacheExecutionContext>? OnHitAction { get; set; }
        public Action<CacheExecutionContext>? OnMissAction { get; set; }
        public bool IsIdempotent { get; set; }
        
        // ETag-specific settings
        public ETagSettings? ETag { get; set; }
    }
    
    public class ETagSettings
    {
        /// <summary>
        /// ETag generation strategy to use
        /// </summary>
        public ETagGenerationStrategy Strategy { get; set; } = ETagGenerationStrategy.ContentHash;
        
        /// <summary>
        /// Whether to include method parameters in ETag generation
        /// </summary>
        public bool IncludeParametersInETag { get; set; } = true;
        
        /// <summary>
        /// Custom ETag generator type (must implement IETagGenerator)
        /// </summary>
        public Type? ETagGeneratorType { get; set; }
        
        /// <summary>
        /// Additional metadata to include in ETag calculation
        /// </summary>
        public string[]? Metadata { get; set; }
        
        /// <summary>
        /// Whether to use weak ETags (W/ prefix)
        /// </summary>
        public bool UseWeakETag { get; set; } = false;
        
        /// <summary>
        /// Custom cache duration for ETag entries (if different from method cache duration)
        /// </summary>
        public TimeSpan? CacheDuration { get; set; }
    }
    
    /// <summary>
    /// Defines strategies for ETag generation.
    /// </summary>
    public enum ETagGenerationStrategy
    {
        /// <summary>
        /// Generate ETag based on content hash (default).
        /// </summary>
        ContentHash,
        
        /// <summary>
        /// Generate ETag based on last modified timestamp.
        /// </summary>
        LastModified,
        
        /// <summary>
        /// Generate ETag based on version number.
        /// </summary>
        Version,
        
        /// <summary>
        /// Use custom ETag generator.
        /// </summary>
        Custom
    }
}
