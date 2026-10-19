using System.Collections.Concurrent;
using System.ComponentModel;
using MethodCache.Extensibility;
using MethodCache.Core.Telemetry;
using MethodCache.Policies;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Infrastructure
{
    /// <summary>
    /// What every generated decorator needs, resolved once per container. Scoped and transient decorators are
    /// created per resolve, so this keeps that to one lookup instead of one per service.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal sealed class DecoratorServices
    {
        private readonly ConcurrentDictionary<Type, object> _sites = new();

        // The registry the decorators' methods are created with: the container's, or one registration's
        private readonly IPolicyRegistry _sitesRegistry;
        internal IPolicyRegistry SitesRegistry => _sitesRegistry;
        internal GeneratedDiagnostics Diagnostics { get; init; } = new(null);
        internal IReadOnlyDictionary<string, string[]> ParameterNames { get; init; } = new Dictionary<string, string[]>();

        /// <summary>
        /// Creates the services a generated decorator takes in its constructor. Public only for generated code.
        /// </summary>
        /// <param name="cacheManager">The cache that stores method results.</param>
        /// <param name="policyRegistry">Resolves each method's cache policy.</param>
        /// <param name="keyGenerator">Builds cache keys from method arguments.</param>
        /// <param name="telemetry">Container-owned Core instrumentation.</param>
        public DecoratorServices(
            ICacheManager cacheManager,
            IPolicyRegistry policyRegistry,
            ICacheKeyGenerator keyGenerator,
            CoreTelemetry? telemetry = null)
        {
            CacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
            FastMemory = cacheManager as InMemoryCacheManager;
            PolicyRegistry = policyRegistry ?? throw new ArgumentNullException(nameof(policyRegistry));
            KeyGenerator = keyGenerator ?? throw new ArgumentNullException(nameof(keyGenerator));
            Telemetry = telemetry ?? FastMemory?.Telemetry;
            _sitesRegistry = policyRegistry;
        }

        private DecoratorServices(DecoratorServices container, RegistrationPolicyRegistry registration, bool recordsKeys = false)
        {
            Diagnostics = container.Diagnostics;
            ParameterNames = container.ParameterNames;
            CacheManager = container.CacheManager;
            // A recording decorator never reads the cache, so every call reaches the load, where its key is recorded
            FastMemory = recordsKeys ? null : container.FastMemory;
            PolicyRegistry = container.PolicyRegistry;
            KeyGenerator = container.KeyGenerator;
            Telemetry = recordsKeys ? null : container.Telemetry;
            RecordsKeys = recordsKeys;
            _sitesRegistry = registration;
        }

        /// <summary>
        /// The services of one registration of an interface, with its own methods: a keyed registration has its
        /// service key in its cache keys, and a scoped or transient one gets a new scope for background loads.
        /// Created once per registration and container.
        /// </summary>
        internal DecoratorServices ForRegistration(string? keySuffix, BackgroundLoadTarget? background)
            => new(this, new RegistrationPolicyRegistry(PolicyRegistry, keySuffix, background));

        /// <summary>
        /// The services of a decorator that records the key of each cached call instead of running it, for
        /// <see cref="IMethodCacheKeys"/>. Its keys match those of the registration with <paramref name="keySuffix"/>.
        /// </summary>
        internal DecoratorServices ForKeyRecording(string? keySuffix)
            => new(this, new RegistrationPolicyRegistry(PolicyRegistry, keySuffix, background: null), recordsKeys: true);

        /// <summary>Whether a load records its key and returns a default value instead of running the method.</summary>
        internal bool RecordsKeys { get; }

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
                provider.GetService<CoreTelemetry>());
        }

        /// <summary>The cache that stores method results.</summary>
        public ICacheManager CacheManager { get; }
        internal InMemoryCacheManager? FastMemory { get; }

        /// <summary>Resolves each method's cache policy.</summary>
        public IPolicyRegistry PolicyRegistry { get; }

        /// <summary>Builds cache keys from method arguments.</summary>
        public ICacheKeyGenerator KeyGenerator { get; }

        /// <summary>Instrumentation owned by this container.</summary>
        internal CoreTelemetry? Telemetry { get; }

        /// <summary>
        /// Returns the instance a generated loader calls. Public only for generated code.
        /// </summary>
        /// <remarks>
        /// Usually <paramref name="service"/>, the decorated instance. In a refresh-ahead, or a fail-safe load that goes
        /// on after its caller got the stale value, a scoped or transient service is resolved again from a new scope
        /// instead, which is disposed when the load ends, so the load never uses an instance whose scope ended.
        /// </remarks>
        /// <param name="method">The cached method being loaded.</param>
        /// <param name="service">The instance the decorator decorates.</param>
        public static TService ForLoad<TService>(CachedMethod method, TService service)
            where TService : class
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            return BackgroundLoadScope.ForLoad(method.Background, service);
        }

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

            return (TSites)_sites.GetOrAdd(typeof(TSites), static (_, state) => state.factory(state.registry), (factory, registry: _sitesRegistry));
        }
    }
}
