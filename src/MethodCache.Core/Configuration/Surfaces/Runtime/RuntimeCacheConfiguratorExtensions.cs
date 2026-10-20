using System.Linq.Expressions;
using MethodCache.Core.PolicyPipeline.Model;

namespace MethodCache;

/// <summary>
/// Names the method to override with an expression instead of its id, for example
/// <c>configurator.UpsertAsync&lt;IUserService&gt;(s =&gt; s.GetUserAsync(Any&lt;int&gt;.Value), o =&gt; o.WithDuration(...))</c>.
/// The arguments in the expression are ignored.
/// </summary>
public static class RuntimeCacheConfiguratorExtensions
{
    /// <summary>
    /// Applies or replaces the runtime override of the cached method <paramref name="method"/> calls, which returns
    /// <c>T</c>, <c>Task&lt;T&gt;</c> or <c>ValueTask&lt;T&gt;</c>.
    /// </summary>
    /// <param name="configurator">The configurator.</param>
    /// <param name="method">Calls the method, such as <c>s =&gt; s.GetUserAsync(Any&lt;int&gt;.Value)</c>.</param>
    /// <param name="configure">Sets the settings the override changes; the others keep their values from the lower layers.</param>
    /// <param name="metadata">Who made the override and why, and when it is removed automatically.</param>
    /// <param name="cancellationToken">Cancels waiting for the method's previous values to be invalidated.</param>
    /// <exception cref="ArgumentException"><paramref name="method"/> does not call a method, or calls one that is not cached.</exception>
    public static ValueTask UpsertAsync<TService>(this IRuntimeCacheConfigurator configurator,
        Expression<Func<TService, object?>> method, Action<CacheEntryOptions.Builder> configure,
        RuntimeOverrideMetadata? metadata = null, CancellationToken cancellationToken = default)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(configurator);
        return configurator.UpsertAsync(MethodIds.ForCachedCall(method), configure, metadata, cancellationToken);
    }

    /// <summary>Removes the runtime override of the cached method <paramref name="method"/> calls.</summary>
    /// <param name="configurator">The configurator.</param>
    /// <param name="method">Calls the method, such as <c>s =&gt; s.GetUserAsync(Any&lt;int&gt;.Value)</c>.</param>
    /// <param name="cancellationToken">Cancels waiting for the override's values to be invalidated.</param>
    /// <exception cref="ArgumentException"><paramref name="method"/> does not call a method, or calls one that is not cached.</exception>
    public static ValueTask RemoveAsync<TService>(this IRuntimeCacheConfigurator configurator,
        Expression<Func<TService, object?>> method, CancellationToken cancellationToken = default)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(configurator);
        return configurator.RemoveAsync(MethodIds.ForCachedCall(method), cancellationToken);
    }
}
