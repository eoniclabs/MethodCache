using System;

namespace MethodCache.ETags.Models
{
    /// <summary>
    /// Metadata describing ETag configuration for a cached method.
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
