using System;
using System.Collections.Generic;
using MethodCache.Core.Infrastructure.Metrics;
using MethodCache.Core.Options;

namespace MethodCache.Core.Runtime;

public sealed class CacheRuntimeOptions
{
    private static readonly IReadOnlyList<Action<CacheContext>> EmptyCallbacks = Array.Empty<Action<CacheContext>>();

    public static CacheRuntimeOptions Empty { get; } = new CacheRuntimeOptions();

    public TimeSpan? SlidingExpiration { get; init; }
    public TimeSpan? RefreshAhead { get; init; }
    public StampedeProtectionOptions? StampedeProtection { get; init; }
    public DistributedLockOptions? DistributedLock { get; init; }
    public ICacheMetrics? Metrics { get; init; }
    public IReadOnlyList<Action<CacheContext>> OnHitCallbacks { get; init; } = EmptyCallbacks;
    public IReadOnlyList<Action<CacheContext>> OnMissCallbacks { get; init; } = EmptyCallbacks;

    public static CacheRuntimeOptions From(CacheEntryOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        return new CacheRuntimeOptions
        {
            SlidingExpiration = options.SlidingExpiration,
            RefreshAhead = options.RefreshAhead,
            StampedeProtection = options.StampedeProtection,
            DistributedLock = options.DistributedLock,
            Metrics = options.Metrics,
            OnHitCallbacks = options.OnHitCallbacks.Count == 0 ? EmptyCallbacks : new List<Action<CacheContext>>(options.OnHitCallbacks),
            OnMissCallbacks = options.OnMissCallbacks.Count == 0 ? EmptyCallbacks : new List<Action<CacheContext>>(options.OnMissCallbacks)
        };
    }
}
