using System.ComponentModel;
using MethodCache.Policies;

namespace MethodCache.Infrastructure;

/// <summary>
/// Helpers for policy sources and generated code; not meant to be called directly.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class CachePolicyMapper
{
    /// <summary>
    /// Returns a copy of the policy with a contribution from the given source added to its <see cref="CachePolicy.Provenance"/>.
    /// </summary>
    /// <param name="policy">The policy to copy.</param>
    /// <param name="sourceId">The source making the contribution.</param>
    /// <param name="fields">The fields the source set.</param>
    /// <param name="timestamp">When the contribution was made.</param>
    /// <param name="metadata">Optional key/value pairs for the contribution.</param>
    /// <param name="notes">An optional free-text note.</param>
    /// <returns>The policy with the contribution recorded.</returns>
    public static CachePolicy AttachContribution(CachePolicy policy, string sourceId, CachePolicyFields fields, DateTimeOffset timestamp, IReadOnlyDictionary<string, string?>? metadata = null, string? notes = null)
    {
        if (policy == null)
        {
            throw new ArgumentNullException(nameof(policy));
        }

        var contribution = new PolicyContribution(sourceId, fields, timestamp, metadata, notes);
        var provenance = policy.Provenance.Append(contribution);
        return policy with { Provenance = provenance };
    }

    /// <summary>
    /// Returns the fields that have a value in the policy.
    /// </summary>
    /// <param name="policy">The policy to inspect.</param>
    /// <returns>The set fields.</returns>
    internal static CachePolicyFields DetectFields(CachePolicy policy)
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

        if (policy.SlidingExpiration.HasValue)
        {
            fields |= CachePolicyFields.SlidingExpiration;
        }

        if (policy.Version.HasValue)
        {
            fields |= CachePolicyFields.Version;
        }

        if (policy.RefreshAhead.HasValue)
        {
            fields |= CachePolicyFields.RefreshAhead;
        }

        if (policy.CacheWhen != null)
        {
            fields |= CachePolicyFields.CacheWhen;
        }

        if (policy.FailSafe.HasValue)
        {
            fields |= CachePolicyFields.FailSafe;
        }

        if (policy.FailSafeTimeout.HasValue)
        {
            fields |= CachePolicyFields.FailSafeTimeout;
        }

        if (policy.FailSafeRetryAfter.HasValue)
        {
            fields |= CachePolicyFields.FailSafeRetryAfter;
        }

        if (policy.Metadata.Count > 0)
        {
            fields |= CachePolicyFields.Metadata;
        }

        if (policy.Enabled.HasValue) fields |= CachePolicyFields.Enabled;
        if (policy.Distributed.HasValue) fields |= CachePolicyFields.Distributed;
        return fields;
    }
}
