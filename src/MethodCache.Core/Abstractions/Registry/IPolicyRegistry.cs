using System.Collections.Generic;
using System.Threading;
using System.ComponentModel;

namespace MethodCache.Policies;

/// <summary>
/// Synchronous access to the effective, merged cache policy of each method.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
internal interface IPolicyRegistry
{
    /// <summary>
    /// Returns the effective policy for a method; a method without any policy gets an empty one.
    /// </summary>
    /// <param name="methodId">The method identifier.</param>
    /// <returns>The resolved policy and the contributions that built it.</returns>
    PolicyResolutionResult GetPolicy(string methodId);

    /// <summary>
    /// Returns the effective policy of every method that has one.
    /// </summary>
    /// <returns>One result per method.</returns>
    IReadOnlyCollection<PolicyResolutionResult> GetAllPolicies();

    /// <summary>
    /// Streams a new result each time a method's effective policy changes.
    /// </summary>
    /// <param name="cancellationToken">Stops the stream.</param>
    /// <returns>The updated policies, as they change.</returns>
    IAsyncEnumerable<PolicyResolutionResult> WatchAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current version number for a policy.
    /// This is a lightweight operation (single volatile read) used by decorators
    /// to detect when their cached policies are stale.
    /// Returns 0 if the policy doesn't exist.
    /// </summary>
    long GetPolicyVersion(string methodId);
}
