using System.Reflection;
using System.Runtime.CompilerServices;
using MethodCache.Abstractions.Registry;
using MethodCache.Core.Configuration;
using MethodCache.Core.Configuration.Surfaces.ConfigFile;
using MethodCache.Core.Configuration.Surfaces.Fluent;
using MethodCache.Core.Configuration.Surfaces.Runtime;
using MethodCache.Core.Infrastructure.Registration;
using MethodCache.Core.Infrastructure.Serialization;
using MethodCache.Core.Management;
using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Core.PolicyPipeline.Resolution;
using MethodCache.Core.PolicyPipeline.Sources;
using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Runtime.Distributed;
using MethodCache.Core.Runtime.Execution;
using MethodCache.Core.Runtime.KeyGeneration;
using MethodCache.Core.Storage.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MethodCache.Core.Infrastructure.Extensions
{
    public static class MethodCacheServiceCollectionExtensions
    {
        /// <summary>
        /// Adds MethodCache services and wraps every service with cache attributes in its generated decorator.
        /// Services you registered before this call keep their lifetime and are decorated in place; interfaces
        /// in the calling assembly that are not registered yet get their implementation registered for you.
        /// Use the overload that takes assemblies when unregistered interfaces live elsewhere.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="configure">Optional configuration action</param>
        /// <returns>The service collection for chaining</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static IServiceCollection AddMethodCache(this IServiceCollection services, Action<IFluentMethodCacheConfiguration>? configure = null)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            return AddMethodCacheCore(services, configure, CallingAssemblies(RuntimeFeature.IsDynamicCodeSupported ? Assembly.GetCallingAssembly() : null));
        }

        /// <summary>
        /// Adds MethodCache services, decorates registered services with cache attributes, and registers the
        /// implementations of unregistered ones declared in the specified assemblies.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="configure">Optional configuration action</param>
        /// <param name="assemblies">Assemblies whose unregistered interfaces to register. If none, the calling assembly.</param>
        /// <returns>The service collection for chaining</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static IServiceCollection AddMethodCache(this IServiceCollection services, Action<IFluentMethodCacheConfiguration>? configure = null, params Assembly[]? assemblies)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            var targetAssemblies = assemblies is { Length: > 0 }
                ? assemblies
                : CallingAssemblies(RuntimeFeature.IsDynamicCodeSupported ? Assembly.GetCallingAssembly() : null);

            return AddMethodCacheCore(services, configure, targetAssemblies);
        }

        private static IServiceCollection AddMethodCacheCore(IServiceCollection services, Action<IFluentMethodCacheConfiguration>? configure, Assembly[]? targetAssemblies)
        {
            EnsureCoreServices(services);

            // Attribute policies come from the source the generator emits, registered with each decorator
            DecorateExistingRegistrations(services);
            RegisterUnregisteredServices(services, targetAssemblies);

            if (configure != null)
            {
                RegisterFluentPolicySource(services, configure);
            }

            return services;
        }

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

            // Parsed here so invalid values fail at startup; reloads are applied while the application runs
            var source = ConfigFilePolicySourceBuilder.FromConfiguration(configuration, sectionName);
            services.AddSingleton<PolicySourceRegistration>(sp =>
            {
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
            AddDefaultCacheManager(services);
            services.TryAddSingleton<ICacheKeyGenerator, FastHashKeyGenerator>();
            services.TryAddSingleton<ICacheMetricsProvider, NullCacheMetricsProvider>();
            services.TryAddSingleton(static sp => new DecoratorServices(
                sp.GetRequiredService<ICacheManager>(),
                sp.GetRequiredService<IPolicyRegistry>(),
                sp.GetRequiredService<ICacheKeyGenerator>(),
                sp.GetService<ICacheMetricsProvider>()));

            PolicyRegistrationExtensions.EnsurePolicyServices(services);

            if (!services.Any(sd => sd.ServiceType == typeof(RuntimePolicyStore)))
            {
                services.AddSingleton<RuntimePolicyStore>();
            }

            if (!services.Any(sd => sd.ImplementationType == typeof(RuntimePolicySource)))
            {
                services.AddSingleton<PolicySourceRegistration>(sp =>
                    new PolicySourceRegistration(
                        new RuntimePolicySource(sp.GetRequiredService<RuntimePolicyStore>()),
                        PolicySourcePriority.RuntimeOverrides));
            }

            services.TryAddSingleton<IRuntimeCacheConfigurator, RuntimeCacheConfigurator>();
        }

        /// <summary>
        /// Registers the in-memory cache manager unless a cache manager is already registered. The same instance
        /// also serves <see cref="IMemoryCache"/>, so statistics and direct entry access stay reachable when
        /// <see cref="ICacheManager"/> is wrapped, for example by OpenTelemetry instrumentation. It is also
        /// administered through <see cref="ICacheAdministrator"/>.
        /// </summary>
        internal static void AddDefaultCacheManager(IServiceCollection services)
        {
            if (services.Any(sd => sd.ServiceType == typeof(ICacheManager)))
            {
                return;
            }

            services.TryAddSingleton(static sp => new InMemoryCacheManager(
                sp.GetRequiredService<ICacheMetricsProvider>(),
                sp.GetService<IOptions<MemoryCacheOptions>>(),
                sp.GetService<TimeProvider>(),
                sp.GetService<DistributedCacheLayer>()));
            services.AddSingleton<ICacheManager>(static sp => sp.GetRequiredService<InMemoryCacheManager>());
            services.TryAddSingleton<IMemoryCache>(static sp => sp.GetRequiredService<InMemoryCacheManager>());
            services.TryAddSingleton<ICacheAdministrator>(static sp => new InMemoryCacheAdministrator(
                sp.GetRequiredService<InMemoryCacheManager>(),
                sp.GetService<IPolicyRegistry>()));
        }

        /// <summary>
        /// Keeps cached results in the registered <see cref="IDistributedCache"/> (Redis, SQL Server, ...) as well as
        /// in memory. An in-memory miss reads the distributed cache before running the method, so instances share
        /// results, and invalidating a key or tag reaches every instance. Register the distributed cache itself
        /// first, for example with <c>AddStackExchangeRedisCache</c>; register an <see cref="IBackplane"/> too so
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

            var options = services.AddOptions<DistributedCacheOptions>();
            if (configure != null)
            {
                options.Configure(configure);
            }

            services.TryAddSingleton<ISerializer, JsonCacheSerializer>();
            services.TryAddSingleton(static sp => new DistributedCacheLayer(
                sp.GetRequiredService<IDistributedCache>(),
                sp.GetRequiredService<ISerializer>(),
                sp.GetRequiredService<IOptions<DistributedCacheOptions>>().Value,
                sp.GetService<IBackplane>(),
                sp.GetService<TimeProvider>()));

            EnsureCoreServices(services);
            return services;
        }

        private static void RegisterFluentPolicySource(IServiceCollection services, Action<IFluentMethodCacheConfiguration> configure)
        {
            services.AddSingleton<PolicySourceRegistration>(_ =>
                new PolicySourceRegistration(new FluentPolicySource(configure), PolicySourcePriority.StartupFluent));
        }

        /// <summary>
        /// Native AOT cannot tell which assembly called, so there every generated decorator counts; all module
        /// initializers have run at startup, so that is every decorator in the application.
        /// </summary>
        private static Assembly[]? CallingAssemblies(Assembly? callingAssembly)
            => callingAssembly == null ? null : new[] { callingAssembly };

        /// <summary>
        /// Wraps services the application registered itself in their generated decorators. The original
        /// registration moves to a private key so the container still creates and disposes the implementation
        /// with its own lifetime, factory or instance, and the decorator takes its place under the interface.
        /// </summary>
        private static void DecorateExistingRegistrations(IServiceCollection services)
        {
            var decorated = GetDecoratedRegistrations(services);

            for (var index = 0; index < services.Count; index++)
            {
                var descriptor = services[index];
                var serviceType = descriptor.ServiceType;

                if (descriptor.IsKeyedService ||
                    !serviceType.IsInterface ||
                    serviceType.ContainsGenericParameters ||
                    decorated.Contains(descriptor) ||
                    !MethodCacheDecorators.TryGet(serviceType, out var registration))
                {
                    continue;
                }

                var innerKey = new DecoratedServiceKey(serviceType);
                services.Add(CreateKeyedCopy(descriptor, innerKey));

                var decorator = ServiceDescriptor.Describe(
                    serviceType,
                    provider => registration.Decorate(provider, provider.GetRequiredKeyedService(serviceType, innerKey)),
                    descriptor.Lifetime);

                // Replacing the original slot keeps registration order, and therefore IEnumerable<T> order
                services[index] = decorator;
                decorated.Add(decorator);
                registration.AddPolicies?.Invoke(services);
            }
        }

        /// <summary>
        /// Registers interfaces with cache attributes that the application has not registered, using the
        /// implementation the generator found next to each one, as a singleton.
        /// </summary>
        private static void RegisterUnregisteredServices(IServiceCollection services, Assembly[]? assemblies)
        {
            var registrations = assemblies == null
                ? MethodCacheDecorators.All()
                : assemblies.Distinct().SelectMany(MethodCacheDecorators.For).ToList();

            var decorated = GetDecoratedRegistrations(services);

            foreach (var registration in registrations)
            {
                // Respect registrations the application made itself (lifetime, factory, test double)
                if (services.Any(descriptor => descriptor.ServiceType == registration.ServiceType && !descriptor.IsKeyedService))
                {
                    continue;
                }

                var implementationType = registration.ImplementationType;
                if (implementationType == null)
                {
                    Console.WriteLine($"Warning: No concrete implementation found for interface {registration.ServiceType.FullName} with cache attributes.");
                    continue;
                }

                services.TryAdd(ServiceDescriptor.Singleton(implementationType, implementationType));

                var decorator = ServiceDescriptor.Singleton(
                    registration.ServiceType,
                    provider => registration.Decorate(provider, provider.GetRequiredService(implementationType)));
                services.Add(decorator);
                decorated.Add(decorator);
                registration.AddPolicies?.Invoke(services);
            }
        }

        private static ServiceDescriptor CreateKeyedCopy(ServiceDescriptor descriptor, object key)
        {
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

        private sealed class DecoratedRegistrations
        {
            public HashSet<ServiceDescriptor> Descriptors { get; } = new();
        }

        // Compared by reference, so two registrations of the same interface each keep their own inner service
        private sealed class DecoratedServiceKey
        {
            public DecoratedServiceKey(Type serviceType) => ServiceType = serviceType;

            public Type ServiceType { get; }

            public override string ToString() => $"MethodCache inner {ServiceType.Name}";
        }
    }
}
