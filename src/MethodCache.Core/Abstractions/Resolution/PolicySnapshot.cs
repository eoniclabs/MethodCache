using System;
using System.Collections.Generic;

namespace MethodCache.Policies;

/// <summary>
/// One method's policy as a single source currently defines it.
/// </summary>
internal sealed class PolicySnapshot
{
    /// <summary>
    /// Creates a snapshot.
    /// </summary>
    /// <param name="sourceId">The source defining the policy.</param>
    /// <param name="methodId">The method the policy applies to.</param>
    /// <param name="policy">The policy values from this source.</param>
    /// <param name="timestamp">When the snapshot was taken.</param>
    /// <param name="metadata">Optional key/value pairs from the source.</param>
    public PolicySnapshot(string sourceId, string methodId, CachePolicy policy, DateTimeOffset timestamp, IReadOnlyDictionary<string, string?>? metadata = null)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            throw new ArgumentException("Source identifier is required", nameof(sourceId));
        }

        if (string.IsNullOrWhiteSpace(methodId))
        {
            throw new ArgumentException("Method identifier is required", nameof(methodId));
        }

        SourceId = sourceId;
        MethodId = methodId;
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        Timestamp = timestamp;
        Metadata = metadata;
    }

    /// <summary>
    /// The source defining the policy.
    /// </summary>
    public string SourceId { get; }
    /// <summary>
    /// The method the policy applies to.
    /// </summary>
    public string MethodId { get; }
    /// <summary>
    /// The policy values from this source.
    /// </summary>
    public CachePolicy Policy { get; }
    /// <summary>
    /// When the snapshot was taken.
    /// </summary>
    public DateTimeOffset Timestamp { get; }
    /// <summary>
    /// Optional key/value pairs from the source.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? Metadata { get; }
}
