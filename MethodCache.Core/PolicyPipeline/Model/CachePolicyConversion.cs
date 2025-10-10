using System;
using System.Collections.Generic;
using MethodCache.Core.Configuration;
using MethodCache.Abstractions.Policies;
using System.Globalization;
using System.Linq;

namespace MethodCache.Core.Configuration.Policies;

public static class CachePolicyConversion
{
    public static CacheMethodSettings ToCacheMethodSettings(CachePolicy policy)
    {
        if (policy == null)
        {
            throw new ArgumentNullException(nameof(policy));
        }

        var settings = new CacheMethodSettings
        {
            Duration = policy.Duration,
            Tags = policy.Tags?.ToList() ?? new List<string>(),
            Version = policy.Version,
            KeyGeneratorType = policy.KeyGeneratorType,
            IsIdempotent = policy.RequireIdempotent ?? false
        };

        if (policy.Metadata.Count > 0)
        {
            ApplyMetadata(settings, policy.Metadata);
        }

        return settings;
    }

    private static void ApplyMetadata(CacheMethodSettings settings, IReadOnlyDictionary<string, string?> metadata)
    {
        if (metadata == null || metadata.Count == 0)
        {
            return;
        }

        if (metadata.ContainsKey("etag.strategy") ||
            metadata.ContainsKey("etag.includeParameters") ||
            metadata.ContainsKey("etag.generatorType") ||
            metadata.ContainsKey("etag.metadata") ||
            metadata.ContainsKey("etag.useWeak") ||
            metadata.ContainsKey("etag.cacheDuration"))
        {
            var etag = new ETagMetadata
            {
                Strategy = Get(metadata, "etag.strategy"),
                IncludeParametersInETag = ParseNullableBool(Get(metadata, "etag.includeParameters")),
                ETagGeneratorType = ParseType(Get(metadata, "etag.generatorType")),
                Metadata = ParseList(Get(metadata, "etag.metadata")),
                UseWeakETag = ParseNullableBool(Get(metadata, "etag.useWeak")),
                CacheDuration = ParseNullableTimeSpan(Get(metadata, "etag.cacheDuration"))
            };

            settings.SetETagMetadata(etag);
        }
    }

    private static string? Get(IReadOnlyDictionary<string, string?> metadata, string key)
        => metadata.TryGetValue(key, out var value) ? value : null;

    private static bool? ParseNullableBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (bool.TryParse(value, out var result))
        {
            return result;
        }

        return null;
    }

    private static Type? ParseType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Type.GetType(value, throwOnError: false);
    }

    private static string[]? ParseList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Trim())
            .Where(v => v.Length > 0)
            .ToArray();
    }

    private static TimeSpan? ParseNullableTimeSpan(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var duration))
        {
            return duration;
        }

        return null;
    }
}
