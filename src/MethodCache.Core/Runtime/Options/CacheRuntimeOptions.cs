using MethodCache.Core.Infrastructure.Metrics;
using MethodCache.Core.Options;
using MethodCache.Core.Runtime.Core;

namespace MethodCache.Core.Runtime.Options;

/// <summary>
/// The parts of <see cref="CacheEntryOptions"/> that only apply to a single <c>GetOrCreateAsync</c> call.
/// </summary>
internal sealed class CacheRuntimeOptions
{
    private static readonly IReadOnlyList<Action<CacheContext>> EmptyCallbacks = Array.Empty<Action<CacheContext>>();

    public static CacheRuntimeOptions Empty { get; } = new CacheRuntimeOptions();

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
            Metrics = options.Metrics,
            OnHitCallbacks = options.OnHitCallbacks.Count == 0 ? EmptyCallbacks : new List<Action<CacheContext>>(options.OnHitCallbacks),
            OnMissCallbacks = options.OnMissCallbacks.Count == 0 ? EmptyCallbacks : new List<Action<CacheContext>>(options.OnMissCallbacks)
        };
    }
}
