using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Runtime;

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

        /// <summary>
        /// Extracts ETag metadata from CacheRuntimeDescriptor.
        /// Parses the existing etag.* keys used by the policy pipeline.
        /// </summary>
        public static ETagMetadata? GetETagMetadata(this CacheRuntimeDescriptor descriptor)
        {
            if (descriptor?.Metadata == null) return null;
            return ParseETagMetadataFromPolicyKeys(descriptor.Metadata);
        }

        /// <summary>
        /// Extracts ETag metadata from CachePolicy.
        /// Parses the existing etag.* keys used by the policy pipeline.
        /// </summary>
        public static ETagMetadata? GetETagMetadata(this CachePolicy policy)
        {
            if (policy?.Metadata == null) return null;
            return ParseETagMetadataFromPolicyKeys(policy.Metadata);
        }

        private static ETagMetadata? ParseETagMetadataFromPolicyKeys(IReadOnlyDictionary<string, string?> metadata)
        {
            // Check if any etag.* keys exist (used by AttributePolicySource and CachePolicyMapper)
            if (!metadata.Keys.Any(k => k.StartsWith("etag.", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var etag = new ETagMetadata
            {
                Strategy = GetValue(metadata, "etag.strategy"),
                IncludeParametersInETag = ParseNullableBool(GetValue(metadata, "etag.includeParameters")),
                ETagGeneratorType = ParseType(GetValue(metadata, "etag.generatorType")),
                Metadata = ParseList(GetValue(metadata, "etag.metadata")),
                UseWeakETag = ParseNullableBool(GetValue(metadata, "etag.useWeak")),
                CacheDuration = ParseNullableTimeSpan(GetValue(metadata, "etag.cacheDuration"))
            };

            return etag;
        }

        private static string? GetValue(IReadOnlyDictionary<string, string?> metadata, string key)
            => metadata.TryGetValue(key, out var value) ? value : null;

        private static bool? ParseNullableBool(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            return bool.TryParse(value, out var result) ? result : null;
        }

        private static Type? ParseType(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            return Type.GetType(value, throwOnError: false);
        }

        private static string[]? ParseList(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            return value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim())
                .Where(v => v.Length > 0)
                .ToArray();
        }

        private static TimeSpan? ParseNullableTimeSpan(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            return TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var duration) ? duration : null;
        }

        /// <summary>
        /// Legacy method for extracting ETag metadata from CacheMethodSettings.
        /// Use GetETagMetadata on CacheRuntimeDescriptor or CachePolicy instead.
        /// </summary>
        [Obsolete("Use GetETagMetadata on CacheRuntimeDescriptor or CachePolicy. Will be removed in v4.0.0")]
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
