using MethodCache.Policies;

namespace MethodCache.Core.PolicyPipeline.Sources;

/// <summary>
/// A policy source held in this process, such as runtime overrides. The resolver applies its changes on the thread
/// that makes them instead of watching it in the background, so a change is in effect when the call that made it
/// returns.
/// </summary>
internal interface IImmediatePolicySource : IPolicySource
{
    /// <summary>The current policies.</summary>
    IReadOnlyCollection<PolicySnapshot> GetSnapshot();

    (IReadOnlyCollection<PolicySnapshot> Snapshots, long Revision) CaptureSnapshots() => (GetSnapshot(), 0);

    /// <summary>Raised synchronously for every change, before the call that made it returns.</summary>
    event Action<PolicyChange>? Changed;
}
