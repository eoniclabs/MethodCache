using System;
using System.Collections.Generic;

using MethodCache.Abstractions.Policies;

namespace MethodCache.Abstractions.Resolution;

public sealed class PolicySnapshot
{
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

    public string SourceId { get; }
    public string MethodId { get; }
    public CachePolicy Policy { get; }
    public DateTimeOffset Timestamp { get; }
    public IReadOnlyDictionary<string, string?>? Metadata { get; }
}
