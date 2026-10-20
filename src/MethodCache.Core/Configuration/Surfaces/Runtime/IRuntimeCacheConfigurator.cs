namespace MethodCache;

/// <summary>
/// Changes the policy of cached methods while the application runs, on this instance only. An override wins over the
/// method's <c>[Cache]</c> attribute, startup code and appsettings until it is removed or its
/// <see cref="RuntimeOverrideMetadata.ExpiresAt"/> passes. Resolve it from dependency injection.
/// </summary>
public interface IRuntimeCacheConfigurator
{
    /// <summary>
    /// Applies or replaces the override of the cached method <paramref name="methodId"/>; it is in effect for the next
    /// call when the returned task completes.
    /// </summary>
    /// <param name="methodId">The method id, such as <c>[MyApp]MyApp.IUserService.GetUserAsync</c>; an overloaded method adds its parameter types, as in <c>[MyApp]MyApp.IUserService.Find(int)</c>.</param>
    /// <param name="configure">Sets the settings the override changes; the others keep their values from the lower layers.</param>
    /// <param name="metadata">Who made the override and why, and when it is removed automatically.</param>
    /// <param name="cancellationToken">Cancels waiting for the method's previous values to be invalidated.</param>
    /// <exception cref="ArgumentException"><paramref name="methodId"/> names no cached method, or a tag template names no parameter of the method.</exception>
    ValueTask UpsertAsync(string methodId, Action<CacheEntryOptions.Builder> configure,
        RuntimeOverrideMetadata? metadata = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the override of <paramref name="methodId"/>, so the method goes back to its other settings, and
    /// invalidates the values cached under the override.
    /// </summary>
    /// <param name="methodId">The method id.</param>
    /// <param name="cancellationToken">Cancels waiting for the override's values to be invalidated.</param>
    /// <exception cref="ArgumentException"><paramref name="methodId"/> names no cached method.</exception>
    ValueTask RemoveAsync(string methodId, CancellationToken cancellationToken = default);

    /// <summary>Removes every override.</summary>
    /// <param name="cancellationToken">Cancels waiting for the overrides' values to be invalidated.</param>
    ValueTask ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>The active overrides of this instance, ordered by method id.</summary>
    IReadOnlyList<RuntimeOverride> GetOverrides();
}
