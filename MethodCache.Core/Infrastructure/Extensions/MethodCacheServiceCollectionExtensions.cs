using System.Collections.ObjectModel;
using System.Reflection;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MethodCache.Core.Infrastructure.Extensions
{
    public static class MethodCacheServiceCollectionExtensions
    {
        /// <summary>
        /// Adds MethodCache services to the dependency injection container.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="configure">Optional configuration action</param>
        /// <returns>The service collection for chaining</returns>
        public static IServiceCollection AddMethodCache(this IServiceCollection services, Action<IFluentMethodCacheConfiguration>? configure = null)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            EnsureCoreServices(services);

            if (configure != null)
            {
                RegisterFluentPolicySource(services, configure);
            }

            return services;
        }

        /// <summary>
        /// Adds MethodCache services and automatically registers all services with cache attributes from the specified assemblies.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="configure">Optional configuration action</param>
        /// <param name="assemblies">Assemblies to scan for services with cache attributes. If null, scans the calling assembly.</param>
        /// <returns>The service collection for chaining</returns>
        public static IServiceCollection AddMethodCache(this IServiceCollection services, Action<IFluentMethodCacheConfiguration>? configure = null, params Assembly[]? assemblies)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            EnsureCoreServices(services);

            var targetAssemblies = assemblies is { Length: > 0 }
                ? assemblies
                : new[] { Assembly.GetCallingAssembly() };

            RegisterAttributePolicySource(services, targetAssemblies);
            services.AddMethodCacheServices(MethodCacheRegistrationOptions.ForAssemblies(targetAssemblies));

            if (configure != null)
            {
                RegisterFluentPolicySource(services, configure);
            }

            return services;
        }

        /// <summary>
        /// Adds MethodCache services and automatically registers all services with cache attributes using the specified options.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="configure">Optional configuration action</param>
        /// <param name="options">Options for automatic service registration</param>
        /// <returns>The service collection for chaining</returns>
        public static IServiceCollection AddMethodCache(this IServiceCollection services, Action<IFluentMethodCacheConfiguration>? configure, MethodCacheRegistrationOptions options)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            EnsureCoreServices(services);

            var assemblies = GetAssembliesToScan(options);
            RegisterAttributePolicySource(services, assemblies);
            services.AddMethodCacheServices(options);

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
            services.TryAddSingleton<ICacheManager, InMemoryCacheManager>();
            services.TryAddSingleton<ICacheKeyGenerator, DefaultCacheKeyGenerator>();
            services.TryAddSingleton<ICacheMetricsProvider, ConsoleCacheMetricsProvider>();

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
        /// Adds MethodCache services configured via the fluent API.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">Fluent configuration delegate.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddMethodCacheFluent(this IServiceCollection services, Action<IFluentMethodCacheConfiguration> configure)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (configure == null) throw new ArgumentNullException(nameof(configure));

            return services.AddMethodCache(configure);
        }

        /// <summary>
        /// Automatically registers services with cache attributes from the specified assemblies.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="assemblies">Assemblies to scan. If null, scans the calling assembly.</param>
        /// <returns>The service collection for chaining</returns>
        public static IServiceCollection AddMethodCacheServices(this IServiceCollection services, Assembly[]? assemblies)
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
        public static IServiceCollection AddMethodCacheServices(this IServiceCollection services, MethodCacheRegistrationOptions options)
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
                    if (type == null || type.IsInterface || type.IsAbstract)
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
            // This method will use reflection to call the generated AddXWithCaching method
            // The source generator creates methods like AddISampleServiceWithCaching

            var interfaceName = interfaceType.Name;
            var methodName = $"Add{interfaceName}WithCaching";

            // Look for the generated extension method in the Microsoft.Extensions.DependencyInjection namespace
            var extensionType = Type.GetType("Microsoft.Extensions.DependencyInjection.MethodCacheServiceCollectionExtensions, " + interfaceType.Assembly.FullName);

            if (extensionType == null)
            {
                // Try to find it in any loaded assembly
                extensionType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a =>
                    {
                        try
                        {
                            return a.GetTypes();
                        }
                        catch (ReflectionTypeLoadException)
                        {
                            return Array.Empty<Type>();
                        }
                    })
                    .FirstOrDefault(t => t.Name == "MethodCacheServiceCollectionExtensions" &&
                                        t.Namespace == "Microsoft.Extensions.DependencyInjection");
            }

            if (extensionType != null)
            {
                var method = extensionType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
                if (method != null)
                {
                    // Create a factory function that resolves the concrete implementation
                    var lifetime = options.ServiceLifetimeResolver?.Invoke(implementationType) ?? options.DefaultServiceLifetime;

                    Func<IServiceProvider, object> factory = provider => provider.GetRequiredService(implementationType);

                    // Call the generated extension method
                    method.Invoke(null, new object[] { services, factory });
                    return;
                }
            }

            // Fallback: Log that we couldn't find the generated method
            Console.WriteLine($"Warning: Could not find generated extension method {methodName} for {interfaceType.FullName}. " +
                            "Make sure the source generator has run and the interface has cache attributes.");
        }

    }
}
