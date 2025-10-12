using MethodCache.Abstractions.Policies;
using MethodCache.Abstractions.Resolution;

namespace MethodCache.Core.PolicyPipeline.Model;

internal static class PolicySnapshotBuilder
{
    public static PolicySnapshot FromPolicy(
        string sourceId,
        string methodId,
        CachePolicy policy,
        CachePolicyFields fields,
        DateTimeOffset timestamp,
        IReadOnlyDictionary<string, string?>? metadata,
        string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            throw new ArgumentException("Source identifier must be provided.", nameof(sourceId));
        }

        if (string.IsNullOrWhiteSpace(methodId))
        {
            throw new ArgumentException("Method identifier must be provided.", nameof(methodId));
        }

        if (policy == null)
        {
            throw new ArgumentNullException(nameof(policy));
        }

        var enriched = CachePolicyMapper.AttachContribution(policy, sourceId, fields, timestamp, metadata, notes);
        return new PolicySnapshot(sourceId, methodId, enriched, timestamp, metadata);
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
