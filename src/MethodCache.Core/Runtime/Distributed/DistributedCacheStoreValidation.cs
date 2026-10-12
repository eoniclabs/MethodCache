using MethodCache.Extensibility;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MethodCache.Core.Runtime.Distributed
{
    /// <summary>
    /// Checks at host startup (through <c>ValidateOnStart</c>) that the distributed cache MethodCache was given
    /// implements <see cref="IAtomicInvalidationStore"/>, so a misconfiguration fails <c>StartAsync</c> instead of
    /// the first request. Without a generic host, <see cref="DistributedCacheLayer"/> still rejects the cache when
    /// the cache manager is first resolved.
    /// </summary>
    internal sealed class DistributedCacheStoreValidation
    {
        internal static string NotAtomicMessage(Type cacheType) =>
            $"The distributed cache ({cacheType.FullName}) must implement IAtomicInvalidationStore so concurrent invalidations cannot move timestamps backwards. " +
            "Use MethodCache.Redis/SqlServer (AddMethodCacheRedis, AddMethodCacheSqlServer) or implement backend-atomic stamps in your custom provider.";

        internal sealed class Validator(IServiceProvider services, string ownCacheKey) : IValidateOptions<DistributedCacheStoreValidation>
        {
            public ValidateOptionsResult Validate(string? name, DistributedCacheStoreValidation options)
            {
                var cache = services.GetKeyedService<IDistributedCache>(ownCacheKey) ?? services.GetService<IDistributedCache>();
                if (cache == null)
                {
                    return ValidateOptionsResult.Fail(
                        "AddMethodCacheDistributedCache() needs an IDistributedCache: register one that implements IAtomicInvalidationStore, " +
                        "or use AddMethodCacheRedis() or AddMethodCacheSqlServer().");
                }

                return cache is IAtomicInvalidationStore
                    ? ValidateOptionsResult.Success
                    : ValidateOptionsResult.Fail(NotAtomicMessage(cache.GetType()));
            }
        }
    }
}
