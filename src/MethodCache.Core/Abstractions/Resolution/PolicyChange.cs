using System;
using System.Collections.Generic;

namespace MethodCache.Policies;

/// <summary>
/// A change to one method's policy reported by a policy source while the app runs.
/// </summary>
public sealed class PolicyChange
{
    /// <summary>
    /// Creates a change.
    /// </summary>
    /// <param name="sourceId">The source reporting the change.</param>
    /// <param name="methodId">The method whose policy changed.</param>
    /// <param name="delta">The fields set or cleared and their new values.</param>
    /// <param name="reason">Why the policy changed.</param>
    /// <param name="timestamp">When the change happened.</param>
    /// <param name="metadata">Optional key/value pairs describing the change.</param>
    public PolicyChange(string sourceId, string methodId, CachePolicyDelta delta, PolicyChangeReason reason, DateTimeOffset timestamp, IReadOnlyDictionary<string, string?>? metadata = null)
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
        Delta = delta ?? throw new ArgumentNullException(nameof(delta));
        Reason = reason;
        Timestamp = timestamp;
        Metadata = metadata;
    }

    /// <summary>
    /// The source reporting the change.
    /// </summary>
    public string SourceId { get; }
    /// <summary>
    /// The method whose policy changed.
    /// </summary>
    public string MethodId { get; }
    /// <summary>
    /// The fields set or cleared and their new values.
    /// </summary>
    public CachePolicyDelta Delta { get; }
    /// <summary>
    /// Why the policy changed.
    /// </summary>
    public PolicyChangeReason Reason { get; }
    /// <summary>
    /// When the change happened.
    /// </summary>
    public DateTimeOffset Timestamp { get; }
    /// <summary>
    /// Optional key/value pairs describing the change.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? Metadata { get; }
}
