namespace MethodCache;

/// <summary>
/// Returns the cache key of a call to a cached method, typed and checked by the compiler, so an entry can be
/// invalidated without writing its key by hand. Resolve it from dependency injection.
/// </summary>
/// <example>
/// <code>
/// await cache.InvalidateKeyAsync(keys.For&lt;IUserService&gt;(users =&gt; users.GetUser(42)));
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
