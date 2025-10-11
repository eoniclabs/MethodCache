using System;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Configuration;
using MethodCache.Core.Runtime;
using MethodCache.ETags.Attributes;

namespace MethodCache.ETags.Configuration
{
    public static class ETagSettingsExtensions
    {
        /// <summary>
        /// Extracts ETag settings from CacheRuntimeDescriptor.
        /// </summary>
        public static ETagSettings? GetETagSettings(this CacheRuntimeDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));

            var metadata = descriptor.GetETagMetadata();
            if (metadata == null) return null;

            return ConvertToETagSettings(metadata);
        }

        /// <summary>
        /// Extracts ETag settings from CachePolicy.
        /// </summary>
        public static ETagSettings? GetETagSettings(this CachePolicy policy)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));

            var metadata = policy.GetETagMetadata();
            if (metadata == null) return null;

            return ConvertToETagSettings(metadata);
        }

        private static ETagSettings ConvertToETagSettings(ETagMetadata metadata)
        {
            var strategy = ETagGenerationStrategy.ContentHash;
            if (!string.IsNullOrWhiteSpace(metadata.Strategy)
                && Enum.TryParse(metadata.Strategy, out ETagGenerationStrategy parsedStrategy))
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
