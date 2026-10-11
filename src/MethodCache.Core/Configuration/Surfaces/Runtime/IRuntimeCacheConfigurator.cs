using System.Collections.Generic;
using System.Threading;
using MethodCache.Abstractions.Policies;
using MethodCache.Abstractions.Resolution;
using MethodCache.Core.Configuration.Surfaces.Fluent;
using MethodCache.Core.Options;
using MethodCache.Core.PolicyPipeline.Model;

namespace MethodCache.Core.Configuration.Surfaces.Runtime;

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
    /// <param name="metadata">Optional runtime override metadata (owner/reason/expiry).</param>
    Task UpsertAsync(string methodId, Action<CacheEntryOptions.Builder> configure, RuntimeOverrideMetadata? metadata = null);

    /// <summary>
    /// Applies or replaces the runtime policy for the specified method using a policy builder.
    /// </summary>
    /// <param name="methodId">Fully qualified method id (e.g. <c>MyApp.Services.IUserService.GetUserAsync</c>).</param>
    /// <param name="configure">Policy builder used to configure cache policy fields directly.</param>
    /// <param name="metadata">Optional runtime override metadata (owner/reason/expiry).</param>
    Task UpsertAsync(string methodId, Action<CachePolicyBuilder> configure, RuntimeOverrideMetadata? metadata = null);

    /// <summary>
    /// Applies or replaces the runtime policy for the specified method using an already constructed <see cref="CachePolicy"/>.
    /// </summary>
    /// <param name="methodId">Fully qualified method id.</param>
    /// <param name="policy">Policy snapshot to apply.</param>
    /// <param name="fields">Optional explicit field mask. When <see cref="CachePolicyFields.None"/> the mask is inferred.</param>
    /// <param name="metadata">Optional runtime override metadata (owner/reason/expiry).</param>
    Task UpsertAsync(string methodId, CachePolicy policy, CachePolicyFields fields = CachePolicyFields.None, RuntimeOverrideMetadata? metadata = null);

    /// <summary>
    /// Applies or replaces multiple overrides in one call.
    /// </summary>
    Task UpsertBatchAsync(IEnumerable<RuntimeOverrideEntry> overrides);

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

    /// <summary>
    /// Returns all active runtime overrides as policy snapshots.
    /// </summary>
    Task<IReadOnlyCollection<PolicySnapshot>> GetOverridesAsync();

    /// <summary>
    /// Returns a single runtime override for the given method id when present.
    /// </summary>
    Task<PolicySnapshot?> GetOverrideAsync(string methodId);

    /// <summary>
    /// Watches runtime override changes (add/update/remove).
    /// </summary>
    IAsyncEnumerable<PolicyChange> WatchAsync(CancellationToken cancellationToken = default);
}
