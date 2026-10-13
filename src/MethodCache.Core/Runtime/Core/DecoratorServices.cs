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

        /// <summary>
        /// Creates the services a generated decorator takes in its constructor. Public only for generated code.
        /// </summary>
        /// <param name="cacheManager">The cache that stores method results.</param>
        /// <param name="policyRegistry">Resolves each method's cache policy.</param>
        /// <param name="keyGenerator">Builds cache keys from method arguments.</param>
        /// <param name="metricsProvider">Receives hit, miss, error and latency reports; optional.</param>
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

        /// <summary>The cache that stores method results.</summary>
        public ICacheManager CacheManager { get; }

        /// <summary>Resolves each method's cache policy.</summary>
        public IPolicyRegistry PolicyRegistry { get; }

        /// <summary>Builds cache keys from method arguments.</summary>
        public ICacheKeyGenerator KeyGenerator { get; }

        /// <summary>Receives hit, miss, error and latency reports; null when none is registered.</summary>
        public ICacheMetricsProvider? MetricsProvider { get; }

        /// <summary>
        /// Returns the instance a generated loader calls. Public only for generated code.
        /// </summary>
        /// <remarks>
        /// Usually <paramref name="service"/>, the decorated instance. In a refresh-ahead, or a fail-safe load that goes
        /// on after its caller got the stale value, a scoped or transient service is resolved again from a new scope
        /// instead, which is disposed when the load ends, so the load never uses an instance whose scope ended.
        /// </remarks>
        /// <param name="decorator">The generated decorator.</param>
        /// <param name="service">The instance it decorates.</param>
        public static TService ForLoad<TService>(object decorator, TService service)
            where TService : class
            => BackgroundLoadScope.ForLoad(decorator, service);

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
