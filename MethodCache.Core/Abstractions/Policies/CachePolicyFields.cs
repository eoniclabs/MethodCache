using System;

namespace MethodCache.Abstractions.Policies;

[Flags]
public enum CachePolicyFields
{
    None = 0,
    Duration = 1 << 0,
    Tags = 1 << 1,
    SlidingExpiration = 1 << 2,
    Version = 1 << 3,
    Metadata = 1 << 4,
    RefreshAhead = 1 << 5
}
