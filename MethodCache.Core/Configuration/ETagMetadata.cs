using System;

namespace MethodCache.Core.Configuration
{
    /// <summary>
    /// Metadata describing ETag configuration for a cached method.
    /// Stored in <see cref="CacheMethodSettings.Metadata"/> so optional packages can enrich behavior
    /// without the core assembly taking hard dependencies.
    /// </summary>
    public class ETagMetadata : ICloneable
    {
        public string? Strategy { get; set; } = "ContentHash";
        public bool? IncludeParametersInETag { get; set; } = true;
        public Type? ETagGeneratorType { get; set; }
        public string[]? Metadata { get; set; }
        public bool? UseWeakETag { get; set; }
        public TimeSpan? CacheDuration { get; set; }

        public object Clone()
        {
            return new ETagMetadata
            {
                Strategy = Strategy,
                IncludeParametersInETag = IncludeParametersInETag,
                ETagGeneratorType = ETagGeneratorType,
                Metadata = Metadata != null ? (string[])Metadata.Clone() : null,
                UseWeakETag = UseWeakETag,
                CacheDuration = CacheDuration
            };
        }
    }
}
