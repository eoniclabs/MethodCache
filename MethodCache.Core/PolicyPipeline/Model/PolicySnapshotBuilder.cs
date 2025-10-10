using System;
using System.Collections.Generic;
using MethodCache.Abstractions.Policies;
using MethodCache.Abstractions.Resolution;
using MethodCache.Core.Configuration.Sources;

namespace MethodCache.Core.Configuration.Policies;

internal static class PolicySnapshotBuilder
{
    public static PolicySnapshot FromConfigEntry(string sourceId, MethodCacheConfigEntry entry, DateTimeOffset timestamp)
    {
        if (entry == null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        var methodId = entry.MethodKey;
        if (string.IsNullOrWhiteSpace(methodId))
        {
            throw new ArgumentException("Configuration entry must include a method key", nameof(entry));
        }

        var (policy, fields) = CachePolicyMapper.FromSettings(entry.Settings);
        var enriched = CachePolicyMapper.AttachContribution(policy, sourceId, fields, timestamp);

        var snapshotMetadata = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["priority"] = entry.Priority.ToString()
        };

        return new PolicySnapshot(sourceId, methodId, enriched, timestamp, snapshotMetadata);
    }

    public static PolicyChange CreateChange(string sourceId, string methodId, CachePolicy policy, CachePolicyFields fields, PolicyChangeReason reason, DateTimeOffset timestamp)
    {
        var clearedFields = CachePolicyFields.Duration |
                            CachePolicyFields.Tags |
                            CachePolicyFields.KeyGenerator |
                            CachePolicyFields.Version |
                            CachePolicyFields.Metadata |
                            CachePolicyFields.RequireIdempotent;

        var delta = new CachePolicyDelta(
            reason == PolicyChangeReason.Removed ? CachePolicyFields.None : fields,
            reason == PolicyChangeReason.Removed ? clearedFields : CachePolicyFields.None,
            policy);
        return new PolicyChange(sourceId, methodId, delta, reason, timestamp);
    }
}
