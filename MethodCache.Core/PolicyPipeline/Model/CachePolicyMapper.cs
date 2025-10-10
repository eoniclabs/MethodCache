using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Configuration;

namespace MethodCache.Core.Configuration.Policies;

public static class CachePolicyMapper
{
    public static (CachePolicy Policy, CachePolicyFields Fields) FromSettings(CacheMethodSettings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        var policy = CachePolicy.Empty;
        var fields = CachePolicyFields.None;

        if (settings.Duration.HasValue)
        {
            policy = policy with { Duration = settings.Duration };
            fields |= CachePolicyFields.Duration;
        }

        if (settings.Tags is { Count: > 0 })
        {
            policy = policy with { Tags = settings.Tags.ToArray() };
            fields |= CachePolicyFields.Tags;
        }

        if (settings.KeyGeneratorType != null)
        {
            policy = policy with { KeyGeneratorType = settings.KeyGeneratorType };
            fields |= CachePolicyFields.KeyGenerator;
        }

        if (settings.Version.HasValue)
        {
            policy = policy with { Version = settings.Version };
            fields |= CachePolicyFields.Version;
        }

        if (settings.IsIdempotent)
        {
            policy = policy with { RequireIdempotent = true };
            fields |= CachePolicyFields.RequireIdempotent;
        }

        var metadata = BuildMetadata(settings);
        if (metadata.Count > 0)
        {
            policy = policy with { Metadata = metadata };
            fields |= CachePolicyFields.Metadata;
        }

        return (policy, fields);
    }

    public static CachePolicy AttachContribution(CachePolicy policy, string sourceId, CachePolicyFields fields, DateTimeOffset timestamp, IReadOnlyDictionary<string, string?>? metadata = null, string? notes = null)
    {
        if (policy == null)
        {
            throw new ArgumentNullException(nameof(policy));
        }

        var contribution = new PolicyContribution(sourceId, fields, PolicyContributionKind.Set, timestamp, metadata, notes);
        var provenance = policy.Provenance.Append(contribution);
        return policy with { Provenance = provenance };
    }

    public static CachePolicyFields DetectFields(CachePolicy policy)
    {
        if (policy == null)
        {
            throw new ArgumentNullException(nameof(policy));
        }

        var fields = CachePolicyFields.None;

        if (policy.Duration.HasValue)
        {
            fields |= CachePolicyFields.Duration;
        }

        if (policy.Tags.Count > 0)
        {
            fields |= CachePolicyFields.Tags;
        }

        if (policy.KeyGeneratorType != null)
        {
            fields |= CachePolicyFields.KeyGenerator;
        }

        if (policy.Version.HasValue)
        {
            fields |= CachePolicyFields.Version;
        }

        if (policy.RequireIdempotent.HasValue)
        {
            fields |= CachePolicyFields.RequireIdempotent;
        }

        if (policy.Metadata.Count > 0)
        {
            fields |= CachePolicyFields.Metadata;
        }

        return fields;
    }

    private static IReadOnlyDictionary<string, string?> BuildMetadata(CacheMethodSettings settings)
    {
        if (settings.Metadata.Count == 0)
        {
            return Array.Empty<KeyValuePair<string, string?>>().ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value);
        }

        var dictionary = new Dictionary<string, string?>(settings.Metadata.Count, StringComparer.Ordinal);

        foreach (var (key, value) in settings.Metadata)
        {
            if (value is null)
            {
                dictionary[key] = null;
                continue;
            }

            if (value is ETagMetadata etag)
            {
                AppendETagMetadata(dictionary, etag);
                continue;
            }

            if (value is Array array)
            {
                dictionary[key] = string.Join(",", array.Cast<object?>().Select(static item => Convert.ToString(item, CultureInfo.InvariantCulture)));
                continue;
            }

            dictionary[key] = Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        return dictionary;
    }

    private static void AppendETagMetadata(IDictionary<string, string?> metadata, ETagMetadata etag)
    {
        metadata["etag.strategy"] = etag.Strategy;
        metadata["etag.includeParameters"] = etag.IncludeParametersInETag?.ToString(CultureInfo.InvariantCulture);
        metadata["etag.generatorType"] = etag.ETagGeneratorType?.AssemblyQualifiedName;
        metadata["etag.metadata"] = etag.Metadata == null ? null : string.Join(",", etag.Metadata);
        metadata["etag.useWeak"] = etag.UseWeakETag?.ToString(CultureInfo.InvariantCulture);
        metadata["etag.cacheDuration"] = etag.CacheDuration?.ToString();
    }
}
