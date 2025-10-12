using System;
using System.Collections.Generic;
using System.Linq;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.Core;
using MethodCache.ETags.Attributes;
using MethodCache.ETags.Extensions;
using MethodCache.ETags.Models;

namespace MethodCache.ETags.Configuration
{
    public static class ETagSettingsExtensions
    {
        /// <summary>
        /// Extracts ETag settings from CacheRuntimePolicy.
        /// </summary>
        public static ETagSettings? GetETagSettings(this CacheRuntimePolicy descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));

            // Extract from metadata dictionary directly since CacheRuntimePolicy doesn't have a GetETagMetadata method
            var metadata = ParseETagMetadataFromPolicy(descriptor.Metadata);
            if (metadata == null) return null;

            return ConvertToETagSettings(metadata);
        }

        private static ETagMetadata? ParseETagMetadataFromPolicy(IReadOnlyDictionary<string, string?> metadata)
        {
            if (metadata == null || !metadata.Keys.Any(k => k.StartsWith("etag.", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            return new ETagMetadata
            {
                Strategy = GetValue(metadata, "etag.strategy"),
                IncludeParametersInETag = ParseNullableBool(GetValue(metadata, "etag.includeParameters")),
                ETagGeneratorType = ParseType(GetValue(metadata, "etag.generatorType")),
                Metadata = ParseList(GetValue(metadata, "etag.metadata")),
                UseWeakETag = ParseNullableBool(GetValue(metadata, "etag.useWeak")),
                CacheDuration = ParseNullableTimeSpan(GetValue(metadata, "etag.cacheDuration"))
            };
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
