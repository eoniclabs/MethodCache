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
}
