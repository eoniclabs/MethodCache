using System;
using System.Collections.Generic;
using System.Linq;
using MethodCache.Abstractions.Policies;

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
}
