using MethodCache.Extensibility;
using MethodCache.Infrastructure;

namespace MethodCache
{
    /// <summary>
    /// Looks up a precomputed cache key through <see cref="ICacheManager.LookupAsync{T}"/>, which reports whether an
    /// entry is present separately from its value, so a cached <c>0</c>, <c>false</c> or <c>null</c> is a hit.
    /// </summary>
    internal static class CacheKeyLookup
    {
        /// <summary>The method name such lookups report.</summary>
        internal const string MethodName = "MethodCache.Fluent";

        private static readonly CacheRuntimePolicy Policy = CacheRuntimePolicy.Empty(MethodName);
        private static readonly ICacheKeyGenerator KeyGenerator = new ExplicitKeyGenerator();

        /// <summary>Reads the entry stored under <paramref name="key"/> without starting a load or refresh.</summary>
        public static ValueTask<CacheLookupResult<T>> LookupAsync<T>(ICacheManager cacheManager, string key)
            => cacheManager.LookupAsync<T>(key, Array.Empty<object>(), Policy, KeyGenerator);

        /// <summary>Uses the method name as the key, which is how the lookup passes its precomputed key.</summary>
        private sealed class ExplicitKeyGenerator : ICacheKeyGenerator
        {
            public string GenerateKey(string methodName, object?[] args, CacheRuntimePolicy policy) => methodName;
        }
    }
}
