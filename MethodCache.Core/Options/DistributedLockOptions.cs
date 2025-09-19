using System;

namespace MethodCache.Core.Options
{
    public sealed record DistributedLockOptions(TimeSpan Timeout, int MaxConcurrency);
}
