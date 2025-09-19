using System;
using MethodCache.Core.Configuration;
using MethodCache.ETags.Attributes;

namespace MethodCache.ETags.Configuration
{
    public static class CacheMethodSettingsExtensions
    {
        public static ETagSettings? GetETagSettings(this CacheMethodSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            var metadata = settings.GetETagMetadata();
            if (metadata == null)
            {
                return null;
            }

            var strategy = ETagGenerationStrategy.ContentHash;
            if (!string.IsNullOrWhiteSpace(metadata.Strategy) && Enum.TryParse(metadata.Strategy, out ETagGenerationStrategy parsedStrategy))
            {
                strategy = parsedStrategy;
            }

            return new ETagSettings
            {
                Strategy = strategy,
                IncludeParametersInETag = metadata.IncludeParametersInETag ?? true,
                ETagGeneratorType = metadata.ETagGeneratorType,
                Metadata = metadata.Metadata,
                UseWeakETag = metadata.UseWeakETag ?? false,
                CacheDuration = metadata.CacheDuration
            };
        }
    }

    public class ETagSettings
    {
        public ETagGenerationStrategy Strategy { get; set; } = ETagGenerationStrategy.ContentHash;
        public bool IncludeParametersInETag { get; set; } = true;
        public Type? ETagGeneratorType { get; set; }
        public string[]? Metadata { get; set; }
        public bool UseWeakETag { get; set; }
        public TimeSpan? CacheDuration { get; set; }
    }
}
