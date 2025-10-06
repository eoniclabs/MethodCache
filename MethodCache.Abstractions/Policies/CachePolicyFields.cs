using System;

namespace MethodCache.Abstractions.Policies;

[Flags]
public enum CachePolicyFields
{
    None = 0,
    Duration = 1 << 0,
    Layers = 1 << 1,
    Tags = 1 << 2,
    Consistency = 1 << 3,
    KeyGenerator = 1 << 4,
    Version = 1 << 5,
    Metadata = 1 << 6,
    RequireIdempotent = 1 << 7
}
