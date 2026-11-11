namespace MethodCache;

/// <summary>
/// Changes the policy of cached methods while the application runs. An override wins over the method's
/// <c>[Cache]</c> attribute, startup code and appsettings until it is removed or its
/// <see cref="RuntimeOverrideMetadata.ExpiresAt"/> passes. Resolve it from dependency injection.
/// </summary>
/// <remarks>
/// With a distributed cache and its backplane, an override is stored in the distributed cache and applies on every
/// instance, including instances that start later; it can't set <c>CacheWhen</c>, <c>DurationFor</c>, <c>TagsFor</c>
/// or <c>SlidingFor</c>. Without them, it applies on this instance only, until it restarts.
/// </remarks>
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
    /// <exception cref="ArgumentException"><paramref name="methodId"/> names no cached method, the override sets nothing or sets <c>WithName</c>, its <see cref="RuntimeOverrideMetadata.ExpiresAt"/> has passed, a tag template names no parameter of the method, the method would refresh entries as soon as they are stored, or the override sets a function while every instance applies it.</exception>
    /// <exception cref="InvalidOperationException">Every instance applies overrides and the distributed cache didn't store the change within its <c>OperationTimeout</c>; nothing changed on this instance.</exception>
    ValueTask UpsertAsync(string methodId, Action<CacheEntryOptions.Builder> configure,
        RuntimeOverrideMetadata? metadata = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts the same override on every cached method in <paramref name="group"/>, replacing the override each of them
    /// had. Each method gets its own override, which <see cref="GetOverrides"/> lists and <see cref="RemoveAsync"/> removes
    /// one by one. A method that joins the group later doesn't get it.
    /// </summary>
    /// <param name="group">The group name, as in <c>[Cache(Group = "pricing")]</c>; case-sensitive.</param>
    /// <param name="configure">Sets the settings the override changes; the others keep their values from the lower layers.</param>
    /// <param name="metadata">Who made the override and why, and when it is removed automatically.</param>
    /// <param name="cancellationToken">Cancels waiting for the methods' previous values to be invalidated.</param>
    /// <exception cref="ArgumentException">No cached method is in <paramref name="group"/>, or the override doesn't fit one of its methods, as for <see cref="UpsertAsync"/>; no method changed.</exception>
    /// <exception cref="InvalidOperationException">Every instance applies overrides and the distributed cache didn't store a change within its <c>OperationTimeout</c>; the methods changed before that keep their new override.</exception>
    ValueTask UpsertGroupAsync(string group, Action<CacheEntryOptions.Builder> configure,
        RuntimeOverrideMetadata? metadata = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts the same override on every cached method, replacing the override each of them had, for example to turn
    /// caching off everywhere during an incident. <see cref="ClearAsync"/> removes them all again.
    /// </summary>
    /// <param name="configure">Sets the settings the override changes; the others keep their values from the lower layers.</param>
    /// <param name="metadata">Who made the override and why, and when it is removed automatically.</param>
    /// <param name="cancellationToken">Cancels waiting for the methods' previous values to be invalidated.</param>
    /// <exception cref="ArgumentException">The override doesn't fit one of the methods, as for <see cref="UpsertAsync"/>; no method changed.</exception>
    /// <exception cref="InvalidOperationException">Every instance applies overrides and the distributed cache didn't store a change within its <c>OperationTimeout</c>; the methods changed before that keep their new override.</exception>
    ValueTask UpsertAllAsync(Action<CacheEntryOptions.Builder> configure,
        RuntimeOverrideMetadata? metadata = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the override of every cached method in <paramref name="group"/>, whichever way it was made.
    /// </summary>
    /// <param name="group">The group name; case-sensitive.</param>
    /// <param name="cancellationToken">Cancels waiting for the overrides' values to be invalidated.</param>
    /// <exception cref="ArgumentException">No cached method is in <paramref name="group"/>.</exception>
    /// <exception cref="InvalidOperationException">Every instance applies overrides and the distributed cache didn't answer within its <c>OperationTimeout</c>; the overrides removed before that stay removed.</exception>
    ValueTask RemoveGroupAsync(string group, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the override of <paramref name="methodId"/>, so the method goes back to its other settings, and
    /// invalidates the values cached under the override.
    /// </summary>
    /// <param name="methodId">The method id.</param>
    /// <param name="cancellationToken">Cancels waiting for the override's values to be invalidated.</param>
    /// <exception cref="ArgumentException"><paramref name="methodId"/> names no cached method.</exception>
    /// <exception cref="InvalidOperationException">Every instance applies overrides and the distributed cache didn't store the change within its <c>OperationTimeout</c>; nothing changed on this instance.</exception>
    ValueTask RemoveAsync(string methodId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every override. When every instance applies overrides, that is every override stored for the cached
    /// methods, also one this instance hasn't read yet.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the overrides' values to be invalidated.</param>
    /// <exception cref="InvalidOperationException">Every instance applies overrides and the distributed cache didn't answer within its <c>OperationTimeout</c>; the overrides removed before that stay removed.</exception>
    ValueTask ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>The overrides active on this instance, ordered by method id; with a backplane, the ones every instance applies.</summary>
    IReadOnlyList<RuntimeOverride> GetOverrides();
}
