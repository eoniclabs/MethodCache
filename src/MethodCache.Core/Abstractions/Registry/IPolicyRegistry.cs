using System.Collections.Generic;
using System.Threading;

using MethodCache.Abstractions.Resolution;

namespace MethodCache.Abstractions.Registry;

public interface IPolicyRegistry
{
    PolicyResolutionResult GetPolicy(string methodId);

    IReadOnlyCollection<PolicyResolutionResult> GetAllPolicies();

    IAsyncEnumerable<PolicyResolutionResult> WatchAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current version number for a policy.
    /// This is a lightweight operation (single volatile read) used by decorators
    /// to detect when their cached policies are stale.
    /// Returns 0 if the policy doesn't exist.
    /// </summary>
    long GetPolicyVersion(string methodId);
}
