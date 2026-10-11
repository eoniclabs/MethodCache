namespace MethodCache.Policies;

/// <summary>
/// Why a <see cref="PolicyChange"/> was reported.
/// </summary>
public enum PolicyChangeReason
{
    /// <summary>
    /// The policy was read when the source first loaded.
    /// </summary>
    InitialLoad = 0,
    /// <summary>
    /// The source now has a policy for a method it had none for.
    /// </summary>
    Added = 1,
    /// <summary>
    /// The source changed an existing policy.
    /// </summary>
    Updated = 2,
    /// <summary>
    /// The source no longer has a policy for the method.
    /// </summary>
    Removed = 3,
    /// <summary>
    /// The source reloaded the policy, for example after its file changed.
    /// </summary>
    Reloaded = 4
}
