using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace MethodCache.Policies;

/// <summary>
/// The effective policy for one method after merging all sources, and how it was built.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
internal sealed class PolicyResolutionResult
{
    /// <summary>
    /// Creates a result.
    /// </summary>
    /// <param name="methodId">The method identifier.</param>
    /// <param name="policy">The merged policy.</param>
    /// <param name="contributions">The contributions that built the policy.</param>
    /// <param name="resolvedAt">When the policy was resolved.</param>
    /// <param name="version">A number that increments on each policy update.</param>
    public PolicyResolutionResult(string methodId, CachePolicy policy, IReadOnlyList<PolicyContribution> contributions, DateTimeOffset resolvedAt, long version = 0)
    {
        if (string.IsNullOrWhiteSpace(methodId))
        {
            throw new ArgumentException("Method identifier is required", nameof(methodId));
        }

        MethodId = methodId;
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        Contributions = contributions ?? throw new ArgumentNullException(nameof(contributions));
        ResolvedAt = resolvedAt;
        Version = version;
    }

    /// <summary>
    /// The method this policy applies to.
    /// </summary>
    public string MethodId { get; }
    /// <summary>
    /// The merged policy.
    /// </summary>
    public CachePolicy Policy { get; }
    /// <summary>
    /// The contributions that built the policy, from each source that set fields.
    /// </summary>
    public IReadOnlyList<PolicyContribution> Contributions { get; }
    /// <summary>
    /// When the policy was resolved.
    /// </summary>
    public DateTimeOffset ResolvedAt { get; }

    /// <summary>
    /// Version number that increments on each policy update.
    /// Used by decorators to detect when cached policies are stale.
    /// </summary>
    public long Version { get; }
    internal CachePolicy? Declaration { get; init; }
    internal IReadOnlyDictionary<string, string> FieldSources { get; init; } = new Dictionary<string, string>();
}
