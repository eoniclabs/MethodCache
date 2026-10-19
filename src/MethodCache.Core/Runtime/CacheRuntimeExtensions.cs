using MethodCache.Infrastructure;

namespace MethodCache;

// Transitional internal execution adapter: generated contract 1 is independent of the public manager surface.
internal static class CacheRuntimeExtensions
{
    internal static Task<T> GetOrCreateFastAsync<T>(this ICacheManager manager, string key, string name,
        Func<Task<T>> factory, CacheRuntimePolicy policy, CancellationToken token = default)
        => manager is ICacheRuntime runtime && manager is MethodCache.Extensibility.InMemoryCacheManager
            ? runtime.GetOrCreateFastAsync(key, name, factory, policy, token)
            : GetOrCreateCustomAsync(manager, key, factory, policy, token);

    private static Task<T> GetOrCreateCustomAsync<T>(ICacheManager manager, string key,
        Func<Task<T>> factory, CacheRuntimePolicy policy, CancellationToken token)
        => manager.GetOrCreateAsync(key, factory, static (load, _) => new ValueTask<T>(load()), CacheEntryOptions.FromRuntime(policy), token).AsTask();

    internal static Task<T> GetOrCreateAsync<T>(this ICacheManager manager, string name, object[] args,
        Func<Task<T>> factory, CacheRuntimePolicy policy, MethodCache.Extensibility.ICacheKeyGenerator generator)
        => manager.GetOrCreateFastAsync(generator.GenerateKey(name, args, policy), name, factory, policy);

    internal static ValueTask<CacheLookupResult<T>> LookupAsync<T>(this ICacheManager manager, string name, object[] args,
        CacheRuntimePolicy policy, MethodCache.Extensibility.ICacheKeyGenerator generator)
        => manager is MethodCache.Extensibility.InMemoryCacheManager memory
            ? memory.TryGetPreparedAsync<T>(generator.GenerateKey(name, args, policy), default)
            : manager.TryGetAsync<T>(generator.GenerateKey(name, args, policy), default);

    internal static ValueTask<T?> TryGetAsync<T>(this ICacheManager manager, string name, object[] args,
        CacheRuntimePolicy policy, MethodCache.Extensibility.ICacheKeyGenerator generator)
        => manager is ICacheRuntime runtime ? runtime.TryGetAsync<T>(name, args, policy, generator)
            : ReadValue<T>(manager, generator.GenerateKey(name, args, policy));

    private static async ValueTask<T?> ReadValue<T>(ICacheManager manager, string key)
        => (await manager.TryGetAsync<T>(key, default).ConfigureAwait(false)).Value;

    internal static ValueTask<T?> TryGetFastAsync<T>(this ICacheManager manager, string key) => ReadValue<T>(manager, key);

    internal static bool TryGetFast<T>(this ICacheManager manager, string key, out T? value)
    {
        if (manager is ICacheRuntime runtime) return runtime.TryGetFast(key, out value);
        value = default;
        return false;
    }

    internal static bool TryGetFast<T>(this ICacheManager manager, ReadOnlySpan<char> key, out T? value)
    {
        if (manager is ICacheRuntime runtime) return runtime.TryGetFast(key, out value);
        value = default;
        return false;
    }

    internal static bool TryGetFastTask<T>(this ICacheManager manager, string key,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Task<T>? value)
    {
        if (manager is ICacheRuntime runtime) return runtime.TryGetFastTask(key, out value);
        value = null;
        return false;
    }

    internal static bool TryGetFastTask<T>(this ICacheManager manager, ReadOnlySpan<char> key,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Task<T>? value)
    {
        if (manager is ICacheRuntime runtime) return runtime.TryGetFastTask(key, out value);
        value = null;
        return false;
    }

    internal static Task InvalidateByKeysAsync(this ICacheManager manager, params string[] keys)
        => manager is MethodCache.Extensibility.InMemoryCacheManager memory
            ? memory.InvalidatePreparedKeysAsync(keys, default).AsTask() : manager.InvalidateKeysAsync(keys, default).AsTask();
    internal static Task InvalidateByTagsAsync(this ICacheManager manager, params string[] tags)
        => manager.InvalidateTagsAsync(tags, default).AsTask();
    internal static Task InvalidateByTagPatternAsync(this ICacheManager manager, string pattern)
        => manager is ICacheRuntime runtime ? runtime.InvalidateByTagPatternAsync(pattern) : throw new NotSupportedException();
}
