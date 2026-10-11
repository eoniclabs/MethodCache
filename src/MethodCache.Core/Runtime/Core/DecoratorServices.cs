using System.Collections.Concurrent;
using System.ComponentModel;
using MethodCache.Extensibility;
using MethodCache.Policies;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Infrastructure
{
    /// <summary>
    /// What every generated decorator needs, resolved once per container. Scoped and transient decorators are
    /// created per resolve, so this keeps that to one lookup instead of one per service.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class DecoratorServices
    {
        private readonly ConcurrentDictionary<Type, object> _sites = new();

        public DecoratorServices(
            ICacheManager cacheManager,
            IPolicyRegistry policyRegistry,
            ICacheKeyGenerator keyGenerator,
            ICacheMetricsProvider? metricsProvider = null)
        {
            CacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
            PolicyRegistry = policyRegistry ?? throw new ArgumentNullException(nameof(policyRegistry));
            KeyGenerator = keyGenerator ?? throw new ArgumentNullException(nameof(keyGenerator));
            MetricsProvider = metricsProvider;
        }

        /// <summary>
        /// The container's shared instance, which <c>AddMethodCache</c> registers. Containers set up without it
        /// get a new one, which works but resolves every service again on each call.
        /// </summary>
        public static DecoratorServices From(IServiceProvider provider)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));

            return provider.GetService<DecoratorServices>() ?? new DecoratorServices(
                provider.GetRequiredService<ICacheManager>(),
                provider.GetRequiredService<IPolicyRegistry>(),
                provider.GetRequiredService<ICacheKeyGenerator>(),
                provider.GetService<ICacheMetricsProvider>());
        }

        public ICacheManager CacheManager { get; }

        public IPolicyRegistry PolicyRegistry { get; }

        public ICacheKeyGenerator KeyGenerator { get; }

        public ICacheMetricsProvider? MetricsProvider { get; }

        /// <summary>
        /// Returns a decorator's methods, creating them on first use, so each decorator type resolves its policies
        /// and builds its key prefixes once per container rather than per instance.
        /// </summary>
        public TSites GetSites<TSites>(Func<IPolicyRegistry, TSites> factory)
            where TSites : class
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            if (_sites.TryGetValue(typeof(TSites), out var existing))
            {
                return (TSites)existing;
            }

            return (TSites)_sites.GetOrAdd(typeof(TSites), static (_, state) => state.factory(state.registry), (factory, registry: PolicyRegistry));
        }
    }
}
