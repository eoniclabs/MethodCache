namespace MethodCache;

/// <summary>
/// Returns the cache key or a tag of a call to a cached method, typed and checked by the compiler, so an entry can be
/// invalidated from code that doesn't go through the cached interface, such as an endpoint that writes to the database
/// directly, without writing the key or tag by hand. Resolve it from dependency injection.
/// </summary>
/// <remarks>
/// Prefer these over building a key or tag string yourself: arguments are formatted in a canonical way (a
/// <see cref="Guid"/> as 32 digits without hyphens, an enum as its number, <c>:</c> and <c>\</c> escaped in strings), so a
/// hand-built string such as <c>$"user:{id}"</c> can silently match nothing.
/// </remarks>
/// <example>
/// <code>
/// await cache.InvalidateKeyAsync(keys.For&lt;IUserService&gt;(users =&gt; users.GetUser(42)));
/// await cache.InvalidateTagAsync(keys.Tag&lt;IUserService&gt;(users =&gt; users.GetUser(42), "user:{id}"));
/// </code>
/// </example>
public interface IMethodCacheKeys
{
    /// <summary>
    /// Returns the key the decorator of <typeparamref name="TService"/> caches <paramref name="call"/> under: the same
    /// method overload, generic arguments, policy version and argument formatting as the real call. The call runs on a
    /// stand-in that records the key, so the method itself never runs.
    /// </summary>
    /// <param name="call">Calls one cached method of <typeparamref name="TService"/>, such as <c>users =&gt; users.GetUser(42)</c>.</param>
    /// <param name="serviceKey">The service key of a keyed registration (<c>AddKeyedScoped</c>), or <c>null</c> for the unkeyed one.</param>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TService"/> has no cached registration with <paramref name="serviceKey"/> in this container, or
    /// <paramref name="call"/> does not call exactly one cached method.
    /// </exception>
    string For<TService>(Func<TService, object?> call, object? serviceKey = null) where TService : class;

    /// <summary>
    /// Returns the tag <paramref name="template"/> gives the entry of <paramref name="call"/>: its placeholders filled in
    /// from the call's arguments exactly as the decorator fills them in, so invalidating the result removes that entry.
    /// The call runs on a stand-in, so the method itself never runs.
    /// </summary>
    /// <param name="call">Calls one cached method of <typeparamref name="TService"/>, such as <c>users =&gt; users.GetUser(42)</c>.</param>
    /// <param name="template">
    /// One of the method's tags, exactly as it is written on the method or in its configuration, such as
    /// <c>"user:{id}"</c>. Keeping it in a <c>const</c> on the interface lets the attribute and this call share it.
    /// </param>
    /// <param name="serviceKey">The service key of a keyed registration (<c>AddKeyedScoped</c>), or <c>null</c> for the unkeyed one.</param>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TService"/> has no cached registration with <paramref name="serviceKey"/> in this container,
    /// <paramref name="call"/> does not call exactly one cached method, or <paramref name="template"/> is not one of that
    /// method's tags.
    /// </exception>
    /// <example>
    /// <code>
    /// // After the endpoint has saved the new price
    /// await cache.InvalidateTagAsync(keys.Tag&lt;IPriceSource&gt;(s =&gt; s.GetRateAsync(provider, model), IPriceSource.RateTag));
    /// </code>
    /// </example>
    string Tag<TService>(Func<TService, object?> call, string template, object? serviceKey = null) where TService : class;

    /// <summary>
    /// Returns the id of the cached method <paramref name="call"/> calls, such as
    /// <c>[MyApp]MyApp.IUserService.GetUser</c>, or <c>[MyApp]MyApp.IUserService.Find(int)</c> for an overloaded method:
    /// the id <see cref="CachedMethodInfo.MethodId"/> and
    /// <see cref="CacheEntryInfo.MethodId"/> give, that <see cref="ICacheAdministrator.ListEntries"/> and
    /// <see cref="ICacheAdministrator.InvalidateMethodAsync"/> take, and that configures the method in appsettings. A
    /// generic method has one id for all its type arguments, and every keyed registration shares the method's id. The
    /// call runs on a stand-in, so the method itself never runs.
    /// </summary>
    /// <param name="call">Calls one cached method of <typeparamref name="TService"/>, such as <c>users =&gt; users.GetUser(0)</c>.</param>
    /// <param name="serviceKey">The service key of a keyed registration (<c>AddKeyedScoped</c>), or <c>null</c> for the unkeyed one.</param>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TService"/> has no cached registration with <paramref name="serviceKey"/> in this container, or
    /// <paramref name="call"/> does not call exactly one cached method.
    /// </exception>
    string MethodId<TService>(Func<TService, object?> call, object? serviceKey = null) where TService : class;
}
