using System;
using System.Collections.Generic;
using MethodCache.Core.Metrics;
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

    /// <summary>
    /// Creates runtime options from legacy CacheMethodSettings during migration.
    /// Will be removed in v4.0.0.
    /// </summary>
    [Obsolete("This is a migration helper and will be removed in v4.0.0")]
    internal static CacheRuntimeOptions FromLegacySettings(Configuration.CacheMethodSettings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        // Note: OnHitAction/OnMissAction take CacheExecutionContext, but CacheRuntimeOptions uses CacheContext
        // For now, we'll skip these callbacks in the FromLegacySettings conversion
        // They will be handled separately by the legacy compatibility layer
        var onHitCallbacks = EmptyCallbacks;
        var onMissCallbacks = EmptyCallbacks;

        return new CacheRuntimeOptions
        {
            SlidingExpiration = settings.SlidingExpiration,
            RefreshAhead = settings.RefreshAhead,
            StampedeProtection = settings.StampedeProtection,
            DistributedLock = settings.DistributedLock,
            Metrics = settings.Metrics,
            OnHitCallbacks = onHitCallbacks,
            OnMissCallbacks = onMissCallbacks
        };
    }
}
