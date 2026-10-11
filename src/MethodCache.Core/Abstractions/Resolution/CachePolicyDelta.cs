using System;

using MethodCache.Abstractions.Policies;

namespace MethodCache.Abstractions.Resolution;

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
