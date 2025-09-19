using System;

namespace MethodCache.Core.Configuration
{
    public static class CacheMethodSettingsExtensions
    {
        private const string ETagMetadataKey = "MethodCache.ETags.Metadata";

        public static void SetETagMetadata(this CacheMethodSettings settings, ETagMetadata metadata)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            settings.Metadata[ETagMetadataKey] = metadata;
        }

        public static ETagMetadata? GetETagMetadata(this CacheMethodSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            return settings.Metadata.TryGetValue(ETagMetadataKey, out var value) ? value as ETagMetadata : null;
        }

        public static void MergeWithDefaultETagMetadata(this CacheMethodSettings settings, ETagMetadata defaults)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (defaults == null) throw new ArgumentNullException(nameof(defaults));

            var metadata = settings.GetETagMetadata();
            if (metadata == null)
            {
                settings.SetETagMetadata((ETagMetadata)defaults.Clone());
                return;
            }

            if (string.IsNullOrEmpty(metadata.Strategy))
            {
                metadata.Strategy = defaults.Strategy;
            }

            if (!metadata.IncludeParametersInETag.HasValue)
            {
                metadata.IncludeParametersInETag = defaults.IncludeParametersInETag;
            }

            if (metadata.ETagGeneratorType == null)
            {
                metadata.ETagGeneratorType = defaults.ETagGeneratorType;
            }

            if (metadata.Metadata == null && defaults.Metadata != null)
            {
                metadata.Metadata = (string[])defaults.Metadata.Clone();
            }

            if (!metadata.UseWeakETag.HasValue)
            {
                metadata.UseWeakETag = defaults.UseWeakETag;
            }

            if (!metadata.CacheDuration.HasValue && defaults.CacheDuration.HasValue)
            {
                metadata.CacheDuration = defaults.CacheDuration;
            }
        }
    }
}
