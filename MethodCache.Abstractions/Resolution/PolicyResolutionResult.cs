using System;
using System.Collections.Generic;

using MethodCache.Abstractions.Policies;

namespace MethodCache.Abstractions.Resolution;

public sealed class PolicyResolutionResult
{
    public PolicyResolutionResult(string methodId, CachePolicy policy, IReadOnlyList<PolicyContribution> contributions, DateTimeOffset resolvedAt)
    {
        if (string.IsNullOrWhiteSpace(methodId))
        {
            throw new ArgumentException("Method identifier is required", nameof(methodId));
        }

        MethodId = methodId;
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        Contributions = contributions ?? throw new ArgumentNullException(nameof(contributions));
        ResolvedAt = resolvedAt;
    }

    public string MethodId { get; }
    public CachePolicy Policy { get; }
    public IReadOnlyList<PolicyContribution> Contributions { get; }
    public DateTimeOffset ResolvedAt { get; }
}
