using MethodCache.Core.Runtime.Http;
using System.Collections.Concurrent;
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
            services.TryAddSingleton<ExplicitCoreRegistration>();
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
            validation.ConfiguresDistributed |= builder.ConfiguresDistributed;
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
        internal static IServiceCollection AddMethodCacheFromConfiguration(this IServiceCollection services, IConfiguration configuration, string sectionName = "MethodCache")
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
                    source.ReloadFailed += error => LogRejectedReload(logger, error);
                }

                return new PolicySourceRegistration(source, PolicySourcePriority.ConfigurationFiles);
            });

            return services;
        }

        /// <summary>
        /// Logs why a reloaded configuration was rejected: the path of the setting and what is wrong with it, never
        /// the value, which may be anything the configuration holds. Errors without a value carry the path in their message.
        /// </summary>
        private static void LogRejectedReload(ILogger logger, Exception error)
        {
            if (ConfigurationValueError.TryGet(error, out var path, out var reason))
            {
                logger.LogError("Ignored reloaded MethodCache configuration: '{ConfigurationPath}' {Reason}. The previous cache policies stay in effect.",
                    path, reason);
            }
            else
            {
                logger.LogError("Ignored reloaded MethodCache configuration: {Reason} The previous cache policies stay in effect.", error.Message);
            }
        }

        private static void EnsureCoreServices(IServiceCollection services)
        {
            services.AddMetrics();
            services.AddOptions<MethodCacheTelemetryOptions>();
            services.TryAddSingleton(static sp => new MethodCache.Core.Telemetry.CoreTelemetry(
                sp.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
                sp.GetRequiredService<IOptions<MethodCacheTelemetryOptions>>(), sp.GetService<TimeProvider>()));
            services.TryAddSingleton(new GeneratedParameters());
            services.TryAddSingleton(static provider => new RegistrationValidationState(
                provider.GetService<RegistrationValidation>(), provider.GetService<ILoggerFactory>()?.CreateLogger("MethodCache.Registration")));
            AddDefaultCacheManager(services);
            services.TryAddSingleton<ICacheKeyGenerator, FastHashKeyGenerator>();
            services.TryAddSingleton(static sp => new DecoratorServices(
                sp.GetRequiredService<ICacheManager>(),
                sp.GetRequiredService<IPolicyRegistry>(),
                sp.GetRequiredService<ICacheKeyGenerator>(),
                sp.GetService<MethodCache.Core.Telemetry.CoreTelemetry>())
            {
                Diagnostics = new GeneratedDiagnostics(sp.GetService<ILoggerFactory>()?.CreateLogger("MethodCache.Generated")),
                ParameterNames = new Dictionary<string, string[]>(sp.GetRequiredService<GeneratedParameters>().Names, StringComparer.Ordinal)
            });

            services.TryAddSingleton(static sp => new GeneratedServices(sp.GetRequiredService<DecoratorServices>()));
            services.TryAddSingleton<IMethodCacheKeys>(static sp => new MethodCacheKeyRecorder(sp, sp.GetRequiredService<GeneratedParameters>()));

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
                sp.GetService<IOptions<MethodCacheMemoryOptions>>(),
                sp.GetService<TimeProvider>(),
                sp.GetService<DistributedCacheLayer>(),
                methodId => (sp.GetRequiredService<MethodCache.Policies.IPolicyRegistry>() as MethodCache.Core.PolicyPipeline.Resolution.PolicyRegistry)?.InvalidateGeneration(methodId),
                sp.GetService<ILoggerFactory>()?.CreateLogger<InMemoryCacheManager>(), sp.GetRequiredService<MethodCache.Core.Telemetry.CoreTelemetry>(),
                sp.GetService<HttpResponseStore>(),
                sp.GetRequiredService<GeneratedParameters>().CreateLookup(() => sp.GetRequiredService<IPolicyRegistry>())));
            services.AddSingleton<ICacheManager>(static sp => sp.GetRequiredService<InMemoryCacheManager>());
            services.TryAddSingleton<IMemoryCache>(static sp => sp.GetRequiredService<InMemoryCacheManager>());
            services.TryAddSingleton<ICacheAdministrator>(CreateAdministrator);
        }

        internal sealed class ExplicitCoreRegistration;

        internal static ICacheAdministrator CreateAdministrator(IServiceProvider services)
        {
            var method = services.GetService<InMemoryCacheManager>();
            var http = services.GetService<HttpResponseStore>();
            if (method != null) return new InMemoryCacheAdministrator(method, services.GetService<IPolicyRegistry>(), http);
            return http != null ? new HttpCacheAdministrator(http) : null!;
        }

        // Shared service key contract: MethodCache.HttpCaching resolves this owned cache before the app's cache.
        // Keep this key stable for the HTTP add-on's owned-storage lookup.
        private const string OwnDistributedCacheKey = MethodCache.Infrastructure.OwnedServices.Key;

        /// <summary>
        /// Keeps cached results in a distributed cache that only MethodCache uses, created by
        /// <paramref name="cacheFactory"/>, as well as in memory. The app's own <see cref="IDistributedCache"/>, if
        /// any, is left alone. The method-result provider must implement <see cref="IAtomicInvalidationStore"/>; otherwise
        /// starting the host fails (or, without a generic host, resolving the cache manager). Integrations such as <c>UseRedis</c> supply this capability.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="cacheFactory">Creates the distributed cache, once</param>
        /// <param name="configure">Optional distributed cache options</param>
        /// <returns>The service collection for chaining</returns>
        internal static IServiceCollection AddMethodCacheDistributedCache(this IServiceCollection services, Func<IServiceProvider, IDistributedCache> cacheFactory, Action<DistributedCacheOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(cacheFactory);

            // UseRedis and UseSqlServer both end here. A second provider would take over storage while the first
            // one's backplane stayed, so it is rejected; the same provider again only adds to its options.
            var provider = cacheFactory.Method.Module.Assembly;
            if (services.Select(static d => d.ImplementationInstance).OfType<DistributedCacheProvider>().FirstOrDefault() is { } existing)
            {
                if (existing.Assembly != provider)
                {
                    throw new InvalidOperationException(
                        $"MethodCache can use one distributed cache (L2), but two were added: one from {existing.Assembly.GetName().Name} " +
                        $"and one from {provider.GetName().Name}. Call only one of UseRedis or UseSqlServer.");
                }
            }
            else
            {
                services.AddSingleton(new DistributedCacheProvider(provider));
            }

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
        internal static IServiceCollection AddMethodCacheDistributedCache(this IServiceCollection services, Action<DistributedCacheOptions>? configure = null)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            MethodCache.Infrastructure.OwnedServices.AddOptions(services);
            var options = services.AddOptions<DistributedCacheOptions>()
                .Validate(o => o.HasEffectiveTagRetention, "TagRetention must exceed ClockSkewTolerance after truncation to whole milliseconds.")
                .ValidateOnStart();
            if (configure != null)
            {
                options.Configure(configure);
            }

            services.TryAddKeyedSingleton<ISerializer>(OwnDistributedCacheKey, static (_, _) => new JsonCacheSerializer());
            services.TryAddSingleton(static sp => new DistributedCacheLayer(
                sp.GetRequiredKeyedService<IDistributedCache>(OwnDistributedCacheKey),
                sp.GetRequiredKeyedService<ISerializer>(OwnDistributedCacheKey),
                sp.GetRequiredKeyedService<DistributedCacheOptions>(OwnDistributedCacheKey),
                sp.GetKeyedService<IBackplane>(OwnDistributedCacheKey),
                sp.GetService<TimeProvider>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<DistributedCacheLayer>(), telemetry: sp.GetRequiredService<MethodCache.Core.Telemetry.CoreTelemetry>()));

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
            var parameters = GetGeneratedParameters(services);
            services.TryAddSingleton(static _ => new ImplementationClaims());

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
                var decoratedRegistration = new DecoratedRegistration(registration, descriptor);
                var decorator = DescribeDecorator(services, decoratedRegistration, parameters);
                services.Add(CreateInnerRegistration(descriptor, decoratedRegistration.InnerKey));

                // Replacing the original slot keeps registration order, and therefore IEnumerable<T> order
                services[index] = decorator;
                decoratedRegistration.Decorator = decorator;
                decorated.Add(decorator);
                parameters.Add(decoratedRegistration);
                registration.AddPolicies?.Invoke(services);
            }

            parameters.GroupRegistrations(services);
        }

        /// <summary>
        /// The decorator registration that takes the place of the application's registration. Each registration has
        /// decorator services of its own, created once per container from the root provider: its cache keys carry
        /// its service key, and its implementation type when the interface has more than one registration under that
        /// key, so two registrations never share entries; a scoped or transient one lets background loads create a
        /// new instance in a new scope.
        /// </summary>
        private static ServiceDescriptor DescribeDecorator(IServiceCollection services, DecoratedRegistration decorated, GeneratedParameters parameters)
        {
            var serviceType = decorated.ServiceType;
            var registration = decorated.Registration;
            foreach (var method in registration.MethodKeys)
                parameters.KeyBindings.Add((MethodCacheKeys.Prefix(serviceType, method.Key), decorated.KeyedSuffix ?? string.Empty, method.Id));
            parameters.Recorders[(serviceType, decorated.ServiceKey)] = decorated;
            var slot = new RegistrationServicesKey();
            Func<IServiceProvider, object> decorate = provider =>
            {
                var inner = provider.GetRequiredKeyedService(typeof(object), decorated.InnerKey);
                var generated = provider.GetRequiredKeyedService<RegistrationServices>(slot).For(inner);
                return registration.Decorate(new RegistrationServiceProvider(provider, generated), inner);
            };
            services.AddKeyedSingleton(slot, (root, _) => new RegistrationServices(root, decorated));

            return decorated.ServiceKey != null
                ? ServiceDescriptor.DescribeKeyed(serviceType, decorated.ServiceKey, (provider, _) => decorate(provider), decorated.Lifetime)
                : ServiceDescriptor.Describe(serviceType, decorate, decorated.Lifetime);
        }

        /// <summary>
        /// Formats an implementation type for the cache keys of a registration that shares its interface and service
        /// key with others: <c>#</c> and the type's assembly-qualified stable name, with <c>\</c>, <c>:</c> and
        /// <c>@</c> escaped so the part always ends at the service key or the key's next part.
        /// </summary>
        internal static string ImplementationPart(Type implementationType)
        {
            var name = MethodCache.Infrastructure.StableTypeIdentity.For(implementationType);
            return "#" + (name.AsSpan().IndexOfAny('\\', ':', '@') < 0
                ? name
                : name.Replace("\\", "\\\\").Replace(":", "\\:").Replace("@", "\\@"));
        }

        /// <summary>The length of an implementation part at the start of <paramref name="rest"/>, up to the service key or next key part.</summary>
        private static int ImplementationPartLength(ReadOnlySpan<char> rest)
        {
            for (var index = 1; index < rest.Length; index++)
            {
                if (rest[index] == '\\') index++;
                else if (rest[index] is ':' or '@') return index;
            }

            return rest.Length;
        }

        private static InvalidOperationException SameImplementation(Type serviceType, object? serviceKey, Type implementationType) => new(
            $"MethodCache found two registrations of {serviceType}{(serviceKey == null ? string.Empty : $" with service key {serviceKey}")} " +
            $"that both use {implementationType}. When an interface has more than one registration, each caches its own results " +
            "under its implementation type, so two of the same type would share cached results. Register them with different " +
            "service keys (AddKeyedSingleton, AddKeyedScoped or AddKeyedTransient), or remove one of them.");

        /// <summary>
        /// One registration MethodCache decorated: what its cache keys carry, and how the decorator finds the
        /// implementation the container creates for it under a private key.
        /// </summary>
        internal sealed class DecoratedRegistration
        {
            internal DecoratedRegistration(DecoratorRegistration registration, ServiceDescriptor descriptor)
            {
                Registration = registration;
                ServiceType = descriptor.ServiceType;
                ServiceKey = descriptor.ServiceKey;
                Lifetime = descriptor.Lifetime;
                InnerKey = new DecoratedServiceKey(descriptor.ServiceType, descriptor.ServiceKey);
                ImplementationType = descriptor.IsKeyedService
                    ? descriptor.KeyedImplementationType ?? descriptor.KeyedImplementationInstance?.GetType()
                    : descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType();
                if (descriptor.IsKeyedService)
                {
                    KeyedSuffix = "@" + (MethodCacheKeys.ServiceKeyPart(descriptor.ServiceKey!) ?? throw new InvalidOperationException(
                        $"The keyed registration of {ServiceType} has a service key of type {descriptor.ServiceKey!.GetType()}. MethodCache puts the " +
                        "service key in the cache keys, so each registration caches its own results, and needs a key with a stable string form: " +
                        "a string, enum, bool, char, integer or Guid. Use a key with a supported stable representation."));
                }
            }

            internal DecoratorRegistration Registration { get; }
            internal Type ServiceType { get; }
            internal object? ServiceKey { get; }
            internal ServiceLifetime Lifetime { get; }

            /// <summary>The private key of the application's own registration, which the container creates the implementation from.</summary>
            internal object InnerKey { get; }

            /// <summary>The registered implementation type or instance type; null for a factory, whose type is known once it runs.</summary>
            internal Type? ImplementationType { get; }

            /// <summary>The service key part of a keyed registration's cache keys (<c>@ecb</c>), else null.</summary>
            internal string? KeyedSuffix { get; }

            /// <summary>The decorator that took the application's registration's place.</summary>
            internal ServiceDescriptor Decorator { get; set; } = null!;

            /// <summary>The registrations of the same interface under the same service key, this one included.</summary>
            internal List<DecoratedRegistration> Group { get; set; } = [];

            /// <summary>
            /// Whether other registrations share this one's interface and service key, so its cache keys carry its
            /// implementation type. Set by every <c>AddMethodCache</c> call and read when the container first builds the
            /// decorator, so a registration decorated by an earlier call follows one added by a later call.
            /// </summary>
            internal bool Shared => Group.Count > 1;

            /// <summary>The implementation type a factory registration created, once one decorator was built.</summary>
            internal Type? ObservedType { get; set; }

            /// <summary>What this registration adds to the method part of its cache keys, given the type it creates.</summary>
            internal string? SuffixFor(Type implementationType)
                => Shared ? ImplementationPart(implementationType) + KeyedSuffix : KeyedSuffix;

            /// <summary>
            /// The suffix for <see cref="IMethodCacheKeys"/>, which records keys without creating the decorator: a
            /// factory registration that shares its interface creates one implementation to learn its type.
            /// </summary>
            internal string? RecordingSuffix(IServiceProvider root)
            {
                if (!Shared) return KeyedSuffix;
                if ((ImplementationType ?? ObservedType) is { } known) return SuffixFor(known);
                if (Lifetime == ServiceLifetime.Singleton) return SuffixFor(root.GetRequiredKeyedService(typeof(object), InnerKey).GetType());
                var scope = root.CreateAsyncScope();
                try
                {
                    return SuffixFor(scope.ServiceProvider.GetRequiredKeyedService(typeof(object), InnerKey).GetType());
                }
                finally
                {
                    var disposal = scope.DisposeAsync();
                    if (!disposal.IsCompletedSuccessfully) disposal.AsTask().GetAwaiter().GetResult();
                }
            }
        }

        /// <summary>
        /// One registration's decorator services in one container. A registration that shares its interface gets one
        /// set per implementation type, and claims the type, so two registrations that create the same type fail
        /// instead of sharing cached results.
        /// </summary>
        private sealed class RegistrationServices(IServiceProvider root, DecoratedRegistration registration)
        {
            private readonly ConcurrentDictionary<Type, GeneratedServices> _byType = new();
            private GeneratedServices? _single;

            internal GeneratedServices For(object inner)
            {
                if (!registration.Shared)
                {
                    return LazyInitializer.EnsureInitialized(ref _single, () => Create(registration.KeyedSuffix));
                }

                var type = registration.ImplementationType ?? inner.GetType();
                if (_byType.TryGetValue(type, out var existing)) return existing;
                if (registration.ImplementationType == null) registration.ObservedType ??= type;
                root.GetRequiredService<ImplementationClaims>().Claim(registration, type);
                return _byType.GetOrAdd(type, static (key, self) => self.Create(self.SuffixFor(key)), this);
            }

            private string? SuffixFor(Type type) => registration.SuffixFor(type);

            private GeneratedServices Create(string? suffix) => new(DecoratorServices.From(root).ForRegistration(suffix,
                registration.Lifetime != ServiceLifetime.Singleton
                    ? new BackgroundLoadTarget(root.GetRequiredService<IServiceScopeFactory>(), typeof(object), registration.InnerKey)
                    : null));
        }

        /// <summary>Which registration uses each implementation type, per interface and service key, in one container.</summary>
        private sealed class ImplementationClaims
        {
            private readonly ConcurrentDictionary<(List<DecoratedRegistration> Group, Type Type), DecoratedRegistration> _owners = new();

            internal void Claim(DecoratedRegistration registration, Type type)
            {
                if (!ReferenceEquals(_owners.GetOrAdd((registration.Group, type), registration), registration))
                {
                    throw SameImplementation(registration.ServiceType, registration.ServiceKey, type);
                }
            }
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

        /// <summary>
        /// The application's registration under MethodCache's private key, so the container still creates, shares
        /// and disposes the implementation as before. It is registered as <see cref="object"/>, not as the interface,
        /// so resolving the interface with <see cref="KeyedService.AnyKey"/> finds only the decorators. The
        /// implementation still sees the application's key: a keyed factory gets it, and a type whose constructor
        /// takes the key is created by <see cref="ServiceKeyActivator"/> instead of the container.
        /// </summary>
        private static ServiceDescriptor CreateInnerRegistration(ServiceDescriptor descriptor, object key)
        {
            var serviceKey = descriptor.ServiceKey;
            if (descriptor.IsKeyedService)
            {
                if (descriptor.KeyedImplementationInstance != null)
                {
                    return new ServiceDescriptor(typeof(object), key, descriptor.KeyedImplementationInstance);
                }

                if (descriptor.KeyedImplementationFactory != null)
                {
                    // The factory sees the key the application registered, not the private one
                    var keyedFactory = descriptor.KeyedImplementationFactory;
                    return new ServiceDescriptor(typeof(object), key, (provider, _) => keyedFactory(provider, serviceKey), descriptor.Lifetime);
                }

                return CreateInnerType(descriptor.KeyedImplementationType!, serviceKey, key, descriptor.Lifetime);
            }

            if (descriptor.ImplementationInstance != null)
            {
                return new ServiceDescriptor(typeof(object), key, descriptor.ImplementationInstance);
            }

            if (descriptor.ImplementationFactory != null)
            {
                var factory = descriptor.ImplementationFactory;
                return new ServiceDescriptor(typeof(object), key, (provider, _) => factory(provider), descriptor.Lifetime);
            }

            return CreateInnerType(descriptor.ImplementationType!, serviceKey: null, key, descriptor.Lifetime);
        }

        private static ServiceDescriptor CreateInnerType(
            [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)] Type implementationType,
            object? serviceKey, object key, ServiceLifetime lifetime)
        {
            if (!ServiceKeyActivator.UsesServiceKey(implementationType))
            {
                return new ServiceDescriptor(typeof(object), key, implementationType, lifetime);
            }

            var activator = new ServiceKeyActivator(implementationType, serviceKey);
            return new ServiceDescriptor(typeof(object), key, (provider, _) => activator.Create(provider), lifetime);
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

        private static GeneratedParameters GetGeneratedParameters(IServiceCollection services)
            => (GeneratedParameters)services.First(d => d.ServiceType == typeof(GeneratedParameters)).ImplementationInstance!;

        internal sealed class GeneratedParameters
        {
            private readonly Dictionary<(Type Service, object? Key), List<DecoratedRegistration>> _groups = [];
            internal Dictionary<string, string[]> Names { get; } = new(StringComparer.Ordinal);

            // The service key part of each registration; an implementation part before it is skipped when matching
            internal List<(string Prefix, string Suffix, string Method)> KeyBindings { get; } = [];

            // What IMethodCacheKeys needs to build a recording decorator for each registration: the last one of an
            // interface and service key, the one GetService and GetKeyedService return
            internal Dictionary<(Type Service, object? Key), DecoratedRegistration> Recorders { get; } = [];

            internal void Add(DecoratedRegistration registration)
            {
                var key = (registration.ServiceType, registration.ServiceKey);
                if (!_groups.TryGetValue(key, out var group)) _groups[key] = group = [];
                group.Add(registration);
                registration.Group = group;
            }

            /// <summary>
            /// Brings each interface's registrations per service key up to date after an <c>AddMethodCache</c> call:
            /// registrations the application removed since are dropped, so a single remaining one keeps its plain keys,
            /// and two registrations of the same implementation type are rejected, since their keys would be the same.
            /// </summary>
            internal void GroupRegistrations(IServiceCollection services)
            {
                foreach (var (key, group) in _groups)
                {
                    group.RemoveAll(registration => !services.Contains(registration.Decorator));
                    if (group.Count > 0) Recorders[key] = group[^1];
                    if (group.Count < 2) continue;
                    var seen = new HashSet<Type>();
                    foreach (var registration in group)
                        if (registration.ImplementationType is { } type && !seen.Add(type))
                            throw SameImplementation(registration.ServiceType, registration.ServiceKey, type);
                }
            }

            internal Func<string, CacheRuntimePolicy?> CreateLookup(Func<IPolicyRegistry> registry)
            {
                var bindings = KeyBindings.OrderByDescending(static item => item.Prefix.Length).ThenByDescending(static item => item.Suffix.Length).ToArray();
                return key => Resolve(key, registry(), bindings);
            }
            private static CacheRuntimePolicy? Resolve(string key, IPolicyRegistry registry, (string Prefix, string Suffix, string Method)[] bindings)
            {
                foreach (var binding in bindings)
                {
                    if (!key.StartsWith(binding.Prefix, StringComparison.Ordinal)) continue;
                    var rest = key.AsSpan(binding.Prefix.Length);
                    if (!rest.IsEmpty && rest[0] is not ':' and not '<' and not '@' and not '#') continue;
                    if (!rest.IsEmpty && rest[0] == '<')
                    {
                        var end = rest.IndexOf('>');
                        if (end < 0) continue;
                        rest = rest[(end + 1)..];
                    }
                    if (!rest.IsEmpty && rest[0] == '#') rest = rest[ImplementationPartLength(rest)..];
                    if (!rest.StartsWith(binding.Suffix, StringComparison.Ordinal)) continue;
                    rest = rest[binding.Suffix.Length..];
                    var policy = PolicyHandle.For(registry, binding.Method).Current;
                    var version = policy.Version is { } number ? ":v" + number.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
                    if (!rest.StartsWith(version, StringComparison.Ordinal)) continue;
                    var tail = rest[version.Length..];
                    if (tail.IsEmpty || tail[0] == ':') return policy;
                }
                return null;
            }
        }

        /// <summary>The distributed cache provider (L2) registered, named by the assembly that registered it.</summary>
        private sealed record DistributedCacheProvider(Assembly Assembly);

        internal sealed class RegistrationValidation(IServiceCollection services)
        {
            internal HashSet<Type> CachedTypes { get; } = [];
            internal HashSet<ServiceDescriptor> Accepted { get; } = [];

            /// <summary>Whether <c>mc.Distributed(...)</c> was called.</summary>
            internal bool ConfiguresDistributed { get; set; }

            internal void Validate()
            {
                foreach (var descriptor in services)
                    if (descriptor.ServiceKey is not DecoratedServiceKey &&
                        (CachedTypes.Contains(descriptor.ServiceType) || descriptor.ServiceType.IsConstructedGenericType && CachedTypes.Contains(descriptor.ServiceType.GetGenericTypeDefinition())) &&
                        !Accepted.Contains(descriptor))
                        throw new InvalidOperationException($"Cacheable service {descriptor.ServiceType} was registered after AddMethodCache and has no generated decorator. Register it before AddMethodCache.");
            }

            /// <summary>
            /// The settings that only apply to a distributed cache, when none is registered: <c>mc.Distributed(...)</c>
            /// and a serializer chosen with <c>mc.UseMessagePack()</c> or <c>mc.UseJson()</c>. Null when there are none.
            /// </summary>
            internal string? UnusedDistributedSettings()
            {
                if (services.Any(static d => d.ServiceType == typeof(DistributedCacheLayer))) return null;
                var serializer = services.Any(static d => d.IsKeyedService && d.ServiceType == typeof(ISerializer) && Equals(d.ServiceKey, OwnDistributedCacheKey));
                return (ConfiguresDistributed, serializer) switch
                {
                    (true, true) => "Distributed(...) and the serializer chosen with UseMessagePack or UseJson do",
                    (true, false) => "Distributed(...) does",
                    (false, true) => "The serializer chosen with UseMessagePack or UseJson does",
                    _ => null
                };
            }
        }
        internal sealed class RegistrationValidationState(RegistrationValidation? specification, ILogger? logger = null)
        {
            private readonly object _gate = new();
            private bool _validated;
            private bool _warned;
            internal void Validate()
            {
                lock (_gate)
                {
                    if (_validated) return;
                    if (!_warned)
                    {
                        _warned = true;
                        if (specification?.UnusedDistributedSettings() is { } unused)
                            logger?.LogWarning("{Settings} nothing without a distributed cache: add UseRedis(...) or UseSqlServer(...) to AddMethodCache, or remove it.", unused);
                    }

                    specification?.Validate();
                    _validated = true;
                }
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
        internal sealed class RegistrationServiceProvider(IServiceProvider provider, GeneratedServices services) : IServiceProvider
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
