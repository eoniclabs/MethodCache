using System;

namespace MethodCache.Policies;

/// <summary>
/// The fields a policy change sets or clears, plus the policy values it carries.
/// </summary>
internal sealed class CachePolicyDelta
{
    /// <summary>
    /// Creates a delta.
    /// </summary>
    /// <param name="setMask">The fields the change sets.</param>
    /// <param name="clearMask">The fields the change clears.</param>
    /// <param name="snapshot">The policy holding the new values.</param>
    public CachePolicyDelta(CachePolicyFields setMask, CachePolicyFields clearMask, CachePolicy snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        SetMask = setMask;
        ClearMask = clearMask;
    }

    /// <summary>
    /// The fields the change sets, taking their values from <see cref="Snapshot"/>.
    /// </summary>
    public CachePolicyFields SetMask { get; }
    /// <summary>
    /// The fields the change clears, such as all fields when a source removes a policy.
    /// </summary>
    public CachePolicyFields ClearMask { get; }
    /// <summary>
    /// The policy holding the values for the fields in <see cref="SetMask"/>.
    /// </summary>
    public CachePolicy Snapshot { get; }

    /// <summary>
    /// True when the change neither sets nor clears any field.
    /// </summary>
    public bool IsEmpty => SetMask == CachePolicyFields.None && ClearMask == CachePolicyFields.None;
}
