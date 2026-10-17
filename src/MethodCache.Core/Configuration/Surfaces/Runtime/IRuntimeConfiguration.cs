using System.Collections.Generic;
using System.Threading;
using MethodCache.Policies;

namespace MethodCache;

/// <summary>
/// Provides an API for applying cache policy overrides at runtime.
/// </summary>
/// <remarks>
/// A runtime override of a method takes precedence over the other sources of that method (its <c>[Cache]</c>
/// attribute, startup code and appsettings) until it is removed. Its tags are added to theirs unless the override
/// calls <see cref="CacheEntryOptions.Builder.ClearTags"/>. Runtime defaults (<c>DefaultPolicy</c> or <c>ForGroup</c>
/// in <see cref="ApplyAsync"/>) are defaults like the startup ones: they only fill in the values a method does not set,
/// so a method's own <c>[Cache]</c> values still win over them.
/// </remarks>
internal interface IRuntimeConfiguration : IRuntimeCacheConfigurator
{
    /// <summary>
    /// Applies or replaces the runtime policy for the specified method using a fluent builder.
    /// </summary>
    /// <param name="methodId">Fully qualified method id (e.g. <c>MyApp.Services.IUserService.GetUserAsync</c>).</param>
    /// <param name="configure">Builder used to configure the cache entry.</param>
    /// <param name="metadata">Optional runtime override metadata (owner/reason/expiry).</param>
    Task UpsertAsync(string methodId, Action<CacheEntryOptions.Builder> configure, RuntimeOverrideMetadata? metadata = null);

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
