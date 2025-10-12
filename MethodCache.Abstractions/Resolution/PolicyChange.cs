using System;
using System.Collections.Generic;

using MethodCache.Abstractions.Policies;

namespace MethodCache.Abstractions.Resolution;

public sealed class PolicyChange
{
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

    public string SourceId { get; }
    public string MethodId { get; }
    public CachePolicyDelta Delta { get; }
    public PolicyChangeReason Reason { get; }
    public DateTimeOffset Timestamp { get; }
    public IReadOnlyDictionary<string, string?>? Metadata { get; }
}
