using System;
using System.Threading.Tasks;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Configuration.Fluent;
using MethodCache.Core.Configuration.Policies;
using MethodCache.Core.Options;

namespace MethodCache.Core.Configuration.Runtime;

/// <summary>
/// Provides an API for applying cache policy overrides at runtime.
/// Runtime overrides take precedence over all other policy sources until removed.
/// </summary>
public interface IRuntimeCacheConfigurator
{
    /// <summary>
    /// Applies or replaces the runtime policy for the specified method using a fluent builder.
    /// </summary>
    /// <param name="methodId">Fully qualified method id (e.g. <c>MyApp.Services.IUserService.GetUserAsync</c>).</param>
    /// <param name="configure">Builder used to configure the cache entry.</param>
    Task UpsertAsync(string methodId, Action<CacheEntryOptions.Builder> configure);

    /// <summary>
    /// Applies or replaces the runtime policy for the specified method using a policy builder.
    /// </summary>
    /// <param name="methodId">Fully qualified method id (e.g. <c>MyApp.Services.IUserService.GetUserAsync</c>).</param>
    /// <param name="configure">Policy builder used to configure cache policy fields directly.</param>
    Task UpsertAsync(string methodId, Action<CachePolicyBuilder> configure);

    /// <summary>
    /// Applies or replaces the runtime policy for the specified method using an already constructed <see cref="CachePolicy"/>.
    /// </summary>
    /// <param name="methodId">Fully qualified method id.</param>
    /// <param name="policy">Policy snapshot to apply.</param>
    /// <param name="fields">Optional explicit field mask. When <see cref="CachePolicyFields.None"/> the mask is inferred.</param>
    Task UpsertAsync(string methodId, CachePolicy policy, CachePolicyFields fields = CachePolicyFields.None);

    /// <summary>
    /// Applies a batch of overrides using the fluent configuration API.
    /// </summary>
    /// <param name="configure">Delegate that configures one or more methods via the fluent builders.</param>
    Task ApplyAsync(Action<IFluentMethodCacheConfiguration> configure);

    /// <summary>
    /// Removes the runtime override for the specified method, if one exists.
    /// </summary>
    Task RemoveAsync(string methodId);

    /// <summary>
    /// Clears all runtime overrides.
    /// </summary>
    Task ClearAsync();
}
