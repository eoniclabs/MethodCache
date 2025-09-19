using System;

namespace MethodCache.Core.Options
{
    public enum StampedeProtectionMode
    {
        None,
        DistributedLock,
        Probabilistic,
        RefreshAhead
    }

    public sealed record StampedeProtectionOptions(StampedeProtectionMode Mode, double Beta, TimeSpan? RefreshAheadWindow);
}
