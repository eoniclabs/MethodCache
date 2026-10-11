using System;

namespace MethodCache.Policies;

public sealed class CachePolicyDelta
{
    public CachePolicyDelta(CachePolicyFields setMask, CachePolicyFields clearMask, CachePolicy snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        SetMask = setMask;
        ClearMask = clearMask;
    }

    public CachePolicyFields SetMask { get; }
    public CachePolicyFields ClearMask { get; }
    public CachePolicy Snapshot { get; }

    public bool IsEmpty => SetMask == CachePolicyFields.None && ClearMask == CachePolicyFields.None;
}
