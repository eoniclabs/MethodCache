using System.CodeDom.Compiler;
using System.Collections.ObjectModel;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using MethodCache.Core.Configuration;
using MethodCache.Core;
using MethodCache.Core.Configuration.Surfaces.ConfigFile;
using MethodCache.Core.Configuration.Surfaces.Fluent;
using MethodCache.Core.Configuration.Surfaces.Runtime;
using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Core.PolicyPipeline.Resolution;
using MethodCache.Core.PolicyPipeline.Sources;
using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.Execution;
using MethodCache.Core.Runtime.KeyGeneration;
using MethodCache.Core.Storage.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

            return AddMethodCacheCore(services, configure, new[] { Assembly.GetCallingAssembly() });
        }

        /// <summary>
        /// Adds MethodCache services and automatically registers all services with cache attributes from the specified assemblies.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="configure">Optional configuration action</param>
        /// <param name="assemblies">Assemblies to scan for services with cache attributes. If null, scans the calling assembly.</param>
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
                : new[] { Assembly.GetCallingAssembly() };

            return AddMethodCacheCore(services, configure, targetAssemblies);
        }

        private static IServiceCollection AddMethodCacheCore(IServiceCollection services, Action<IFluentMethodCacheConfiguration>? configure, Assembly[] targetAssemblies)
        {
            EnsureCoreServices(services);

            RegisterAttributePolicySource(services, targetAssemblies);
            DecorateExistingRegistrations(services);
            services.AddMethodCacheServices(MethodCacheRegistrationOptions.ForAssemblies(targetAssemblies));

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

            var source = ConfigFilePolicySourceBuilder.FromConfiguration(configuration, sectionName);
            services.AddSingleton<PolicySourceRegistration>(_ =>
                new PolicySourceRegistration(source, PolicySourcePriority.ConfigurationFiles));

            return services;
        }

        private static void EnsureCoreServices(IServiceCollection services)
        {
            AddDefaultCacheManager(services);
            services.TryAddSingleton<ICacheKeyGenerator, DefaultCacheKeyGenerator>();
            services.TryAddSingleton<ICacheMetricsProvider, NullCacheMetricsProvider>();

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
        /// <see cref="ICacheManager"/> is wrapped, for example by OpenTelemetry instrumentation.
        /// </summary>
        internal static void AddDefaultCacheManager(IServiceCollection services)
        {
            if (services.Any(sd => sd.ServiceType == typeof(ICacheManager)))
            {
                return;
            }

            services.TryAddSingleton<InMemoryCacheManager>();
            services.AddSingleton<ICacheManager>(static sp => sp.GetRequiredService<InMemoryCacheManager>());
            services.TryAddSingleton<IMemoryCache>(static sp => sp.GetRequiredService<InMemoryCacheManager>());
        }

        private static void RegisterFluentPolicySource(IServiceCollection services, Action<IFluentMethodCacheConfiguration> configure)
        {
            services.AddSingleton<PolicySourceRegistration>(_ =>
                new PolicySourceRegistration(new FluentPolicySource(configure), PolicySourcePriority.StartupFluent));
        }

        private static void RegisterAttributePolicySource(IServiceCollection services, Assembly[] assemblies)
        {
            if (assemblies == null || assemblies.Length == 0)
            {
                throw new ArgumentException("At least one assembly must be provided.", nameof(assemblies));
            }

            services.AddSingleton<PolicySourceRegistration>(_ =>
                new PolicySourceRegistration(new AttributePolicySource(assemblies), PolicySourcePriority.Attributes));
        }

        /// <summary>
        /// Automatically registers services with cache attributes from the specified assemblies.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="assemblies">Assemblies to scan. If null, scans the calling assembly.</param>
        /// <returns>The service collection for chaining</returns>
        internal static IServiceCollection AddMethodCacheServices(this IServiceCollection services, Assembly[]? assemblies)
        {
            var options = new MethodCacheRegistrationOptions
            {
                Assemblies = assemblies?.Length > 0 ? assemblies : new[] { Assembly.GetCallingAssembly() }
            };

            return services.AddMethodCacheServices(options);
        }

        /// <summary>
        /// Automatically registers services with cache attributes using the specified options.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="options">Registration options</param>
        /// <returns>The service collection for chaining</returns>
        internal static IServiceCollection AddMethodCacheServices(this IServiceCollection services, MethodCacheRegistrationOptions options)
        {
            var assembliesToScan = GetAssembliesToScan(options);
            var typeLookup = BuildTypeLookup(assembliesToScan);
            var interfacesWithCacheAttributes = FindInterfacesWithCacheAttributes(typeLookup, options);
            var implementationCandidates = BuildImplementationCandidates(typeLookup, options);

            foreach (var interfaceType in interfacesWithCacheAttributes)
            {
                RegisterServiceWithCaching(services, interfaceType, options, implementationCandidates);
            }

            return services;
        }

        private static Assembly[] GetAssembliesToScan(MethodCacheRegistrationOptions options)
        {
            var initialAssemblies = options.Assemblies?.Length > 0
                ? new List<Assembly>(options.Assemblies!)
                : new List<Assembly> { Assembly.GetCallingAssembly() };

            if (options.ScanReferencedAssemblies)
            {
                var seen = new HashSet<string>(initialAssemblies.Select(static a => a.FullName ?? string.Empty), StringComparer.Ordinal);
                var queue = new Queue<Assembly>(initialAssemblies);

                while (queue.Count > 0)
                {
                    var assembly = queue.Dequeue();
                    foreach (var reference in assembly.GetReferencedAssemblies())
                    {
                        if (!seen.Add(reference.FullName))
                        {
                            continue;
                        }

                        var loaded = Assembly.Load(reference);
                        initialAssemblies.Add(loaded);
                        queue.Enqueue(loaded);
                    }
                }
            }

            return initialAssemblies.Distinct().ToArray();
        }

        private static IReadOnlyDictionary<Assembly, Type[]> BuildTypeLookup(IEnumerable<Assembly> assemblies)
        {
            var map = new Dictionary<Assembly, Type[]>();

            foreach (var assembly in assemblies)
            {
                try
                {
                    map[assembly] = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    var loadable = ex.Types.Where(static t => t != null).Cast<Type>().ToArray();
                    map[assembly] = loadable;
                }
            }

            return new ReadOnlyDictionary<Assembly, Type[]>(map);
        }

        private static List<Type> FindInterfacesWithCacheAttributes(IReadOnlyDictionary<Assembly, Type[]> typeLookup, MethodCacheRegistrationOptions options)
        {
            var interfaces = new HashSet<Type>();

            foreach (var types in typeLookup.Values)
            {
                foreach (var type in types)
                {
                    if (type == null || !type.IsInterface)
                    {
                        continue;
                    }

                    if (!(options.InterfaceFilter?.Invoke(type) ?? true))
                    {
                        continue;
                    }

                    if (HasCacheAttributes(type))
                    {
                        interfaces.Add(type);
                    }
                }
            }

            return interfaces.ToList();
        }

        private static IReadOnlyList<Type> BuildImplementationCandidates(IReadOnlyDictionary<Assembly, Type[]> typeLookup, MethodCacheRegistrationOptions options)
        {
            var candidates = new List<Type>();
            var seen = new HashSet<Type>();

            foreach (var types in typeLookup.Values)
            {
                foreach (var type in types)
                {
                    if (type == null || type.IsInterface || type.IsAbstract || IsGeneratedDecorator(type))
                    {
                        continue;
                    }

                    if (!(options.ImplementationFilter?.Invoke(type) ?? true))
                    {
                        continue;
                    }

                    if (seen.Add(type))
                    {
                        candidates.Add(type);
                    }
                }
            }

            return candidates;
        }

        // Generated decorators implement the interface they wrap, so they must never be picked as its implementation
        private static bool IsGeneratedDecorator(Type type)
        {
            return type.GetCustomAttributes(typeof(GeneratedCodeAttribute), false)
                .OfType<GeneratedCodeAttribute>()
                .Any(attribute => attribute.Tool == "MethodCacheGenerator");
        }

        private static bool HasCacheAttributes(Type interfaceType)
        {
            return interfaceType.GetMethods()
                .Any(m => m.GetCustomAttributes(typeof(CacheAttribute), false).Any() ||
                         m.GetCustomAttributes(typeof(CacheInvalidateAttribute), false).Any());
        }

        private static void RegisterServiceWithCaching(
            IServiceCollection services,
            Type interfaceType,
            MethodCacheRegistrationOptions options,
            IReadOnlyList<Type> implementationCandidates)
        {
            // Respect registrations the application made itself (lifetime, factory, test double)
            if (services.Any(descriptor => descriptor.ServiceType == interfaceType))
            {
                return;
            }

            // Find concrete implementation
            var implementationType = FindImplementationType(interfaceType, options, implementationCandidates);

            if (implementationType == null)
            {
                var message = $"No concrete implementation found for interface {interfaceType.FullName} with cache attributes.";
                if (options.ThrowOnMissingImplementation)
                {
                    throw new InvalidOperationException(message);
                }

                // Log warning (for now, just skip - in a real implementation we'd use ILogger)
                Console.WriteLine($"Warning: {message}");
                return;
            }

            // Register the concrete implementation if requested
            if (options.RegisterConcreteImplementations)
            {
                var lifetime = options.ServiceLifetimeResolver?.Invoke(implementationType) ?? options.DefaultServiceLifetime;
                RegisterService(services, implementationType, implementationType, lifetime);
            }

            // Register the cached interface using the generated extension method
            RegisterCachedService(services, interfaceType, implementationType, options);
        }

        private static Type? FindImplementationType(Type interfaceType, MethodCacheRegistrationOptions options, IReadOnlyList<Type> implementationCandidates)
        {
            var matches = implementationCandidates
                .Where(interfaceType.IsAssignableFrom)
                .ToList();

            if (matches.Count == 0)
            {
                return null;
            }

            if (matches.Count == 1)
            {
                return matches[0];
            }

            var preferredName = interfaceType.Name.StartsWith("I", StringComparison.Ordinal)
                ? interfaceType.Name.Substring(1)
                : interfaceType.Name;

            var preferred = matches.FirstOrDefault(t => string.Equals(t.Name, preferredName, StringComparison.Ordinal));
            return preferred ?? matches[0];
        }

        private static void RegisterService(IServiceCollection services, Type serviceType, Type implementationType, ServiceLifetime lifetime)
        {
            switch (lifetime)
            {
                case ServiceLifetime.Singleton:
                    services.AddSingleton(serviceType, implementationType);
                    break;
                case ServiceLifetime.Scoped:
                    services.AddScoped(serviceType, implementationType);
                    break;
                case ServiceLifetime.Transient:
                    services.AddTransient(serviceType, implementationType);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, null);
            }
        }

        private static void RegisterCachedService(IServiceCollection services, Type interfaceType, Type implementationType, MethodCacheRegistrationOptions options)
        {
            // The decorator gets the same lifetime as the implementation it wraps, so a scoped
            // implementation is never captured by a singleton decorator
            var lifetime = options.ServiceLifetimeResolver?.Invoke(implementationType) ?? options.DefaultServiceLifetime;
            var method = FindGeneratedRegistration(interfaceType, lifetime);

            if (method == null)
            {
                Console.WriteLine($"Warning: Could not find a generated cache decorator for {interfaceType.FullName}. " +
                                  "Make sure the source generator has run and the interface has cache attributes.");
                return;
            }

            // Expression: (IServiceProvider provider) => (TInterface)provider.GetRequiredService<TImplementation>()
            var providerParam = Expression.Parameter(typeof(IServiceProvider), "provider");
            var getRequiredServiceMethod = typeof(ServiceProviderServiceExtensions)
                .GetMethods()
                .First(m => m.Name == nameof(ServiceProviderServiceExtensions.GetRequiredService)
                            && m.IsGenericMethodDefinition
                            && m.GetParameters().Length == 1)
                .MakeGenericMethod(implementationType);
            var getServiceCall = Expression.Call(null, getRequiredServiceMethod, providerParam);
            var factory = CreateTypedFactory(interfaceType, Expression.Convert(getServiceCall, interfaceType), providerParam);

            method.Invoke(null, new object[] { services, factory });
            var decorator = services[^1];
            GetDecoratedRegistrations(services).Add(decorator);
        }

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
                    !HasCacheAttributes(serviceType))
                {
                    continue;
                }

                var method = FindGeneratedRegistration(serviceType, descriptor.Lifetime);
                if (method == null)
                {
                    continue;
                }

                var innerKey = new DecoratedServiceKey(serviceType);
                services.Add(CreateKeyedCopy(descriptor, innerKey));

                // Expression: (IServiceProvider provider) => (TInterface)provider.GetRequiredKeyedService(typeof(TInterface), innerKey)
                var providerParam = Expression.Parameter(typeof(IServiceProvider), "provider");
                var getKeyedServiceMethod = typeof(ServiceProviderKeyedServiceExtensions)
                    .GetMethod(nameof(ServiceProviderKeyedServiceExtensions.GetRequiredKeyedService), new[] { typeof(IServiceProvider), typeof(Type), typeof(object) })!;
                var getServiceCall = Expression.Call(
                    null,
                    getKeyedServiceMethod,
                    providerParam,
                    Expression.Constant(serviceType, typeof(Type)),
                    Expression.Constant(innerKey, typeof(object)));
                var factory = CreateTypedFactory(serviceType, Expression.Convert(getServiceCall, serviceType), providerParam);

                method.Invoke(null, new object[] { services, factory });

                // The generated method appends the decorator; move it into the original slot so
                // registration order (and therefore IEnumerable<T> order) is unchanged
                var decorator = services[^1];
                services.RemoveAt(services.Count - 1);
                services[index] = decorator;
                decorated.Add(decorator);
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

        private static Delegate CreateTypedFactory(Type interfaceType, Expression body, ParameterExpression providerParam)
        {
            // The generated methods take Func<IServiceProvider, TInterface>, not Func<IServiceProvider, object>
            var factoryDelegateType = typeof(Func<,>).MakeGenericType(typeof(IServiceProvider), interfaceType);
            return Expression.Lambda(factoryDelegateType, body, providerParam).Compile();
        }

        private static MethodInfo? FindGeneratedRegistration(Type interfaceType, ServiceLifetime lifetime)
        {
            // The generator emits Add{Interface}WithCaching{Lifetime} into the assembly that declares the interface,
            // so look there first and only fall back to other loaded assemblies when it is missing
            const string extensionTypeName = "Microsoft.Extensions.DependencyInjection.MethodCacheServiceCollectionExtensions";
            var methodName = $"Add{interfaceType.Name}WithCaching{lifetime}";
            var parameterTypes = new[] { typeof(IServiceCollection), typeof(Func<,>).MakeGenericType(typeof(IServiceProvider), interfaceType) };

            MethodInfo? FindIn(Assembly assembly) =>
                assembly.GetType(extensionTypeName)?.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, null, parameterTypes, null);

            return FindIn(interfaceType.Assembly)
                ?? AppDomain.CurrentDomain.GetAssemblies().Select(FindIn).FirstOrDefault(m => m != null);
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
