using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Configuration;

namespace MethodCache.Core.Configuration.Policies;

public static class CachePolicyMapper
{
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

    public static void AppendETagMetadata(IDictionary<string, string?> metadata, ETagMetadata etag)
    {
        metadata["etag.strategy"] = etag.Strategy;
        metadata["etag.includeParameters"] = etag.IncludeParametersInETag?.ToString(CultureInfo.InvariantCulture);
        metadata["etag.generatorType"] = etag.ETagGeneratorType?.AssemblyQualifiedName;
        metadata["etag.metadata"] = etag.Metadata == null ? null : string.Join(",", etag.Metadata);
        metadata["etag.useWeak"] = etag.UseWeakETag?.ToString(CultureInfo.InvariantCulture);
        metadata["etag.cacheDuration"] = etag.CacheDuration?.ToString();
    }
}
