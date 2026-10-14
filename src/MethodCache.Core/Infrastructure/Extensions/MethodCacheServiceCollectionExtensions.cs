using System.Reflection;
using System.Runtime.CompilerServices;
using MethodCache;
using MethodCache.Core.Configuration.Surfaces.ConfigFile;
using MethodCache.Core.Configuration.Surfaces.Runtime;
using MethodCache.Core.Management;
using MethodCache.Core.PolicyPipeline.Resolution;
using MethodCache.Core.PolicyPipeline.Sources;
using MethodCache.Core.Runtime.Distributed;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using MethodCache.Generated;
using MethodCache.Policies;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// Registers MethodCache in an <see cref="IServiceCollection"/>.
    /// </summary>
    public static class MethodCacheServiceCollectionExtensions
    {
        /// <summary>Adds MethodCache and decorates explicitly registered services with their generated modules.</summary>
        public static IServiceCollection AddMethodCache(this IServiceCollection services)
            => AddMethodCache(services, static _ => { });

        /// <summary>Configures startup policy and generated modules, then decorates existing registrations.</summary>
        public static IServiceCollection AddMethodCache(this IServiceCollection services, Action<MethodCacheBuilder> configureBuilder)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configureBuilder);
            var builder = new MethodCacheBuilder(services);
            configureBuilder(builder);
            var modules = GeneratedCode.Describe(builder.Assemblies, services.Select(static d => d.ServiceType));
            var catalog = new ModuleCatalog(modules, builder.Assemblies.ToArray());
            var parameters = services.FirstOrDefault(d => d.ServiceType == typeof(GeneratedParameters))?.ImplementationInstance as GeneratedParameters;
            if (parameters == null) services.AddSingleton(parameters = new GeneratedParameters());
            EnsureCoreServices(services);
            if (builder.AutoLifetime is { } lifetime) RegisterUnregisteredServices(services, catalog, lifetime);
            DecorateExistingRegistrations(services, catalog);
            // Compile each container's startup policy when its sources load, preserving host-start validation.
            services.AddSingleton<PolicySourceRegistration>(_ => new PolicySourceRegistration(
                new FluentPolicySource(builder.Configuration.BuildMethodPolicies()), PolicySourcePriority.StartupFluent));
            var validation = GetRegistrationValidation(services);
            validation.CachedTypes.UnionWith(modules.SelectMany(static m => m.DeclaredServices));
            validation.Accepted.UnionWith(GetDecoratedRegistrations(services));
            return services;
        }

        /// <summary>
        /// Reads cache policies for <c>[Cache]</c> methods from a configuration section, such as <c>"MethodCache"</c> in
        /// <c>appsettings.json</c>. These win over attributes and startup code, and changes apply when the configuration
        /// reloads. The section is read when the host starts (without a generic host, on first use), so configuration
        /// sources added after this call count too. An invalid value fails startup; an invalid reload is logged and the
        /// previous policies stay.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="configuration">The application's configuration</param>
        /// <param name="sectionName">The section to read policies from</param>
        /// <returns>The service collection for chaining</returns>
        public static IServiceCollection AddMethodCacheFromConfiguration(this IServiceCollection services, IConfiguration configuration, string sectionName = "MethodCache")
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            EnsureCoreServices(services);

            // Read when the policies load, so sources added to the configuration after this call are included
            services.AddSingleton<PolicySourceRegistration>(sp =>
            {
                var source = ConfigFilePolicySourceBuilder.FromConfiguration(configuration, sectionName);
                var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("MethodCache.Configuration");
                if (logger != null)
                {
                    source.ReloadFailed += ex => logger.LogError(ex, "Ignored reloaded MethodCache configuration; the previous cache policies stay in effect.");
                }

                return new PolicySourceRegistration(source, PolicySourcePriority.ConfigurationFiles);
            });

            return services;
        }

        private static void EnsureCoreServices(IServiceCollection services)
        {
            services.TryAddSingleton(new GeneratedParameters());
            AddDefaultCacheManager(services);
            services.TryAddSingleton<ICacheKeyGenerator, FastHashKeyGenerator>();
            services.TryAddSingleton<ICacheMetricsProvider, NullCacheMetricsProvider>();
            services.TryAddSingleton(static sp => new DecoratorServices(
                sp.GetRequiredService<ICacheManager>(),
                sp.GetRequiredService<IPolicyRegistry>(),
                sp.GetRequiredService<ICacheKeyGenerator>(),
                sp.GetService<ICacheMetricsProvider>())
            {
                Diagnostics = new GeneratedDiagnostics(sp.GetService<ILoggerFactory>()?.CreateLogger("MethodCache.Generated")),
                ParameterNames = new Dictionary<string, string[]>(sp.GetRequiredService<GeneratedParameters>().Names, StringComparer.Ordinal)
            });

            services.TryAddSingleton(static sp => new GeneratedServices(sp.GetRequiredService<DecoratorServices>()));

            PolicyRegistrationExtensions.EnsurePolicyServices(services);

            // The store and its source are registered together, once, however many Add* methods are called
            if (!services.Any(sd => sd.ServiceType == typeof(RuntimePolicyStore)))
            {
                services.AddSingleton<RuntimePolicyStore>();
                services.AddSingleton<PolicySourceRegistration>(sp =>
                    new PolicySourceRegistration(
                        new RuntimePolicySource(sp.GetRequiredService<RuntimePolicyStore>()),
                        PolicySourcePriority.RuntimeOverrides));
            }

            services.TryAddSingleton<IRuntimeCacheConfigurator, RuntimeCacheConfigurator>();

            // A generic host loads the policies when it starts, so misconfiguration fails startup instead of the
            // first call; without one, the first cached call loads them and fails the same way
            services.AddOptions<PolicyStartupValidation>().ValidateOnStart();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PolicyStartupValidation>, PolicyStartupValidation.Validator>());
        }

        /// <summary>
        /// Registers the in-memory cache manager unless a cache manager is already registered. The same instance
        /// also serves <see cref="IMemoryCache"/>, so statistics and direct entry access stay reachable when
        /// <see cref="ICacheManager"/> is wrapped, for example by OpenTelemetry instrumentation. It is also
        /// administered through <see cref="ICacheAdministrator"/>.
        /// </summary>
        internal static void AddDefaultCacheManager(IServiceCollection services)
        {
            if (services.Any(sd => !sd.IsKeyedService && sd.ServiceType == typeof(ICacheManager)))
            {
                return;
            }

            services.TryAddSingleton(static sp => new InMemoryCacheManager(
                sp.GetRequiredService<ICacheMetricsProvider>(),
                sp.GetService<IOptions<MethodCacheMemoryOptions>>(),
                sp.GetService<TimeProvider>(),
                sp.GetService<DistributedCacheLayer>()));
            services.AddSingleton<ICacheManager>(static sp => sp.GetRequiredService<InMemoryCacheManager>());
            services.TryAddSingleton<IMemoryCache>(static sp => sp.GetRequiredService<InMemoryCacheManager>());
            services.TryAddSingleton<ICacheAdministrator>(static sp => new InMemoryCacheAdministrator(
                sp.GetRequiredService<InMemoryCacheManager>(),
                sp.GetService<IPolicyRegistry>()));
        }

        // Shared service key contract: MethodCache.HttpCaching resolves this owned cache before the app's cache.
        // Keep this key stable for the HTTP add-on's owned-storage lookup.
        private const string OwnDistributedCacheKey = "MethodCache";

        /// <summary>
        /// Keeps cached results in a distributed cache that only MethodCache uses, created by
        /// <paramref name="cacheFactory"/>, as well as in memory. The app's own <see cref="IDistributedCache"/>, if
        /// any, is left alone. The method-result provider must implement <see cref="IAtomicInvalidationStore"/>; otherwise
        /// starting the host fails (or, without a generic host, resolving the cache manager). Integrations such as <c>AddMethodCacheRedis</c> supply this capability.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="cacheFactory">Creates the distributed cache, once</param>
        /// <param name="configure">Optional distributed cache options</param>
        /// <returns>The service collection for chaining</returns>
        public static IServiceCollection AddMethodCacheDistributedCache(this IServiceCollection services, Func<IServiceProvider, IDistributedCache> cacheFactory, Action<DistributedCacheOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(cacheFactory);

            services.AddKeyedSingleton<IDistributedCache>(OwnDistributedCacheKey, (sp, _) => cacheFactory(sp));
            return services.AddMethodCacheDistributedCache(configure);
        }

        /// <summary>
        /// Keeps cached results in the registered <see cref="IDistributedCache"/> (Redis, SQL Server, ...) as well as
        /// in memory. An in-memory miss reads the distributed cache before running the method, so instances share
        /// results, and invalidating a key or tag reaches every instance. The registered cache must implement
        /// <see cref="IAtomicInvalidationStore"/> with backend atomic stamp writes; otherwise starting the host
        /// fails (or, without a generic host, resolving the cache manager). Register the distributed cache itself first; register an <see cref="IBackplane"/> too so
        /// other instances also drop their in-memory copies at once.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="configure">Optional distributed cache options</param>
        /// <returns>The service collection for chaining</returns>
        public static IServiceCollection AddMethodCacheDistributedCache(this IServiceCollection services, Action<DistributedCacheOptions>? configure = null)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            var options = services.AddOptions<DistributedCacheOptions>()
                .Validate(o => o.HasEffectiveTagRetention, "TagRetention must exceed ClockSkewTolerance after truncation to whole milliseconds.")
                .ValidateOnStart();
            if (configure != null)
            {
                options.Configure(configure);
            }

            services.TryAddSingleton<ISerializer, JsonCacheSerializer>();
            services.TryAddSingleton(static sp => new DistributedCacheLayer(
                sp.GetKeyedService<IDistributedCache>(OwnDistributedCacheKey) ?? sp.GetRequiredService<IDistributedCache>(),
                sp.GetRequiredService<ISerializer>(),
                sp.GetRequiredService<IOptions<DistributedCacheOptions>>().Value,
                sp.GetService<IBackplane>(),
                sp.GetService<TimeProvider>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<DistributedCacheLayer>()));

            // A generic host checks the cache when it starts; without one, the layer above rejects it on first use
            services.AddOptions<DistributedCacheStoreValidation>().ValidateOnStart();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DistributedCacheStoreValidation>, DistributedCacheStoreValidation.Validator>(
                static sp => new DistributedCacheStoreValidation.Validator(sp, OwnDistributedCacheKey)));

            EnsureCoreServices(services);
            return services;
        }

        /// <summary>
        /// Wraps services the application registered itself, keyed ones included, in their generated decorators.
        /// The original registration moves to a private key so the container still creates and disposes the
        /// implementation with its own lifetime, factory or instance, and the decorator takes its place under the
        /// interface and its service key.
        /// </summary>
        private static void DecorateExistingRegistrations(IServiceCollection services, ModuleCatalog catalog)
        {
            var decorated = GetDecoratedRegistrations(services);

            for (var index = 0; index < services.Count; index++)
            {
                var descriptor = services[index];
                var serviceType = descriptor.ServiceType;

                // Private inner registrations and already decorated descriptors are not wrapped again.
                if (descriptor.ServiceKey is DecoratedServiceKey ||
                    !serviceType.IsInterface ||
                    decorated.Contains(descriptor) ||
                    !catalog.TryGet(serviceType, out var registration))
                {
                    continue;
                }

                if (serviceType.ContainsGenericParameters || ReferenceEquals(descriptor.ServiceKey, KeyedService.AnyKey))
                    throw new InvalidOperationException($"MethodCache cannot bind the open generic or AnyKey registration of {serviceType}. Register concrete closed types with explicit keys.");
                var innerKey = new DecoratedServiceKey(serviceType, descriptor.ServiceKey);
                var decorator = DescribeDecorator(services, descriptor, registration, innerKey);
                services.Add(CreateKeyedCopy(descriptor, innerKey));

                // Replacing the original slot keeps registration order, and therefore IEnumerable<T> order
                services[index] = decorator;
                decorated.Add(decorator);
                registration.AddPolicies?.Invoke(services);
            }
        }

        /// <summary>
        /// The decorator registration that takes the place of <paramref name="descriptor"/>. An unkeyed singleton
        /// uses the container's decorator services. A keyed registration puts its service key in its cache keys, so
        /// two registrations of one interface never share entries, and a scoped or transient one lets background
        /// loads create a new instance in a new scope; those get decorator services of their own, created once per
        /// container from the root provider.
        /// </summary>
        private static ServiceDescriptor DescribeDecorator(IServiceCollection services, ServiceDescriptor descriptor, DecoratorRegistration registration, DecoratedServiceKey innerKey)
        {
            var serviceType = descriptor.ServiceType;
            string? keySuffix = null;
            if (descriptor.IsKeyedService)
            {
                keySuffix = "@" + (MethodCacheKeys.ServiceKeyPart(descriptor.ServiceKey!) ?? throw new InvalidOperationException(
                    $"The keyed registration of {serviceType} has a service key of type {descriptor.ServiceKey!.GetType()}. MethodCache puts the " +
                    "service key in the cache keys, so each registration caches its own results, and needs a key with a stable string form: " +
                    "a string, enum, bool, char, integer or Guid. Use a key with a supported stable representation."));
            }

            var background = descriptor.Lifetime != ServiceLifetime.Singleton;
            var slot = new RegistrationServicesKey();
            Func<IServiceProvider, object> decorate = provider => registration.Decorate(
                new RegistrationServiceProvider(provider, provider.GetRequiredKeyedService<GeneratedServices>(slot)),
                provider.GetRequiredKeyedService(serviceType, innerKey));
            services.AddKeyedSingleton(slot, (root, _) => new GeneratedServices(DecoratorServices.From(root).ForRegistration(
                keySuffix,
                background ? new BackgroundLoadTarget(root.GetRequiredService<IServiceScopeFactory>(), serviceType, innerKey) : null)));

            return descriptor.IsKeyedService
                ? ServiceDescriptor.DescribeKeyed(serviceType, descriptor.ServiceKey, (provider, _) => decorate(provider), descriptor.Lifetime)
                : ServiceDescriptor.Describe(serviceType, decorate, descriptor.Lifetime);
        }

        /// <summary>
        /// Registers missing interfaces only when exactly one generated candidate exists, using the explicit lifetime.
        /// </summary>
        private static void RegisterUnregisteredServices(IServiceCollection services, ModuleCatalog catalog, ServiceLifetime lifetime)
        {
            foreach (var registration in catalog.AutoRegistrations())
            {
                if (services.Any(d => d.ServiceType == registration.ServiceType)) continue;
                var candidates = catalog.Candidates(registration).DistinctBy(static c => c.ImplementationType).ToArray();
                if (candidates.Length != 1)
                    throw new InvalidOperationException($"MethodCache AutoRegister for {registration.ServiceType} in {registration.GeneratedIn.GetName().Name} needs exactly one accessible implementation; found {candidates.Length}: {string.Join(", ", candidates.Select(static t => t.ImplementationType.FullName))}.");
                services.Add(ServiceDescriptor.Describe(registration.ServiceType, candidates[0].ImplementationType, lifetime));
            }
        }

        private static ServiceDescriptor CreateKeyedCopy(ServiceDescriptor descriptor, object key)
        {
            if (descriptor.IsKeyedService)
            {
                if (descriptor.KeyedImplementationInstance != null)
                {
                    return new ServiceDescriptor(descriptor.ServiceType, key, descriptor.KeyedImplementationInstance);
                }

                if (descriptor.KeyedImplementationFactory != null)
                {
                    // The factory sees the key the application registered, not the private one
                    var keyedFactory = descriptor.KeyedImplementationFactory;
                    var serviceKey = descriptor.ServiceKey;
                    return new ServiceDescriptor(descriptor.ServiceType, key, (provider, _) => keyedFactory(provider, serviceKey), descriptor.Lifetime);
                }

                return new ServiceDescriptor(descriptor.ServiceType, key, descriptor.KeyedImplementationType!, descriptor.Lifetime);
            }

            if (descriptor.ImplementationInstance != null)
            {
                return new ServiceDescriptor(descriptor.ServiceType, key, descriptor.ImplementationInstance);
            }

            if (descriptor.ImplementationFactory != null)
            {
                var factory = descriptor.ImplementationFactory;
                return new ServiceDescriptor(descriptor.ServiceType, key, (provider, _) => factory(provider), descriptor.Lifetime);
            }

            return new ServiceDescriptor(descriptor.ServiceType, key, descriptor.ImplementationType!, descriptor.Lifetime);
        }

        private static HashSet<ServiceDescriptor> GetDecoratedRegistrations(IServiceCollection services)
        {
            // Remembers which registrations are already decorators, so calling AddMethodCache twice never wraps a decorator again
            var existing = services.FirstOrDefault(d => d.ServiceType == typeof(DecoratedRegistrations))?.ImplementationInstance;
            if (existing is DecoratedRegistrations registrations)
            {
                return registrations.Descriptors;
            }

            var created = new DecoratedRegistrations();
            services.AddSingleton(created);
            return created.Descriptors;
        }

        private sealed class ModuleCatalog(GeneratedModuleBuilder[] modules, Assembly[] selected)
        {
            internal bool TryGet(Type serviceType, out DecoratorRegistration registration)
            {
                var candidates = modules.SelectMany(static m => m.Decorators)
                    .Where(d => d.ServiceType == serviceType || serviceType.IsGenericTypeDefinition && d.ServiceType.IsConstructedGenericType && d.ServiceType.GetGenericTypeDefinition() == serviceType).ToArray();
                var chosen = candidates.Where(d => selected.Contains(d.GeneratedIn)).ToArray();
                if (chosen.Length == 1) { registration = chosen[0]; return true; }
                if (chosen.Length == 0 && candidates.Length == 1) { registration = candidates[0]; return true; }
                if (candidates.Length != 0)
                    throw new InvalidOperationException($"MethodCache has ambiguous modules for {serviceType}: {string.Join(", ", candidates.Select(static c => c.GeneratedIn.GetName().Name))}. Select exactly one with FromAssemblyOf<T>().");
                var definition = serviceType.IsConstructedGenericType ? serviceType.GetGenericTypeDefinition() : serviceType;
                if (modules.Any(m => m.DeclaredServices.Contains(definition)))
                    throw new InvalidOperationException($"MethodCache has no supported generated binding for {serviceType}. Register a concrete closed service with an implementation visible to its generated module; open generic and AnyKey registrations are unsupported.");
                registration = null!;
                return false;
            }
            internal IEnumerable<DecoratorRegistration> AutoRegistrations()
            {
                var types = modules.Where(m => selected.Length == 0 || selected.Contains(m.Module.GetType().Assembly))
                    .SelectMany(static m => m.Decorators).Select(static d => d.ServiceType).Distinct();
                foreach (var type in types) if (TryGet(type, out var registration)) yield return registration;
            }
            internal IEnumerable<CandidateRegistration> Candidates(DecoratorRegistration registration)
                => modules.Where(m => m.Module.GetType().Assembly == registration.GeneratedIn)
                    .SelectMany(m => m.Candidates.GetValueOrDefault(registration.ServiceType) ?? []);
        }

        internal sealed class GeneratedParameters
        {
            internal Dictionary<string, string[]> Names { get; } = new(StringComparer.Ordinal);
        }

        internal sealed class RegistrationValidation(IServiceCollection services)
        {
            internal HashSet<Type> CachedTypes { get; } = [];
            internal HashSet<ServiceDescriptor> Accepted { get; } = [];
            internal void Validate()
            {
                foreach (var descriptor in services)
                    if (descriptor.ServiceKey is not DecoratedServiceKey &&
                        (CachedTypes.Contains(descriptor.ServiceType) || descriptor.ServiceType.IsConstructedGenericType && CachedTypes.Contains(descriptor.ServiceType.GetGenericTypeDefinition())) &&
                        !Accepted.Contains(descriptor))
                        throw new InvalidOperationException($"Cacheable service {descriptor.ServiceType} was registered after AddMethodCache and has no generated decorator. Register it before AddMethodCache.");
            }
        }
        private static RegistrationValidation GetRegistrationValidation(IServiceCollection services)
        {
            var existing = services.FirstOrDefault(d => d.ServiceType == typeof(RegistrationValidation))?.ImplementationInstance as RegistrationValidation;
            if (existing != null) return existing;
            var created = new RegistrationValidation(services);
            services.AddSingleton(created);
            return created;
        }

        private sealed class DecoratedRegistrations
        {
            public HashSet<ServiceDescriptor> Descriptors { get; } = new();
        }

        // Compared by reference, so two registrations of the same interface each keep their own inner service.
        // Holds the application's own service key, if any, for diagnostics.
        /// <summary>The private key of one registration's <see cref="DecoratorServices"/>.</summary>
        private sealed class RegistrationServicesKey
        {
        }

        /// <summary>
        /// The provider a generated decorator is created with: the requesting one, except that it returns the
        /// registration's own <see cref="DecoratorServices"/>.
        /// </summary>
        private sealed class RegistrationServiceProvider(IServiceProvider provider, GeneratedServices services) : IServiceProvider
        {
            public object? GetService(Type serviceType)
                => serviceType == typeof(GeneratedServices) ? services : provider.GetService(serviceType);
        }

        private sealed class DecoratedServiceKey
        {
            public DecoratedServiceKey(Type serviceType, object? serviceKey)
            {
                ServiceType = serviceType;
                ServiceKey = serviceKey;
            }

            public Type ServiceType { get; }

            public object? ServiceKey { get; }

            public override string ToString() => ServiceKey == null
                ? $"MethodCache inner {ServiceType.Name}"
                : $"MethodCache inner {ServiceType.Name} ({ServiceKey})";
        }
    }
}
