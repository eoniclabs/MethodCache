using MethodCache.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace MethodCache.Core
{
    public static class MethodCacheServiceCollectionExtensions
    {
        /// <summary>
        /// Adds MethodCache services to the dependency injection container.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="configure">Optional configuration action</param>
        /// <returns>The service collection for chaining</returns>
        public static IServiceCollection AddMethodCache(this IServiceCollection services, Action<IMethodCacheConfiguration>? configure = null)
        {
            var configuration = new MethodCacheConfiguration();
            configure?.Invoke(configuration);

            services.AddSingleton<IMethodCacheConfiguration>(configuration);
            services.AddSingleton<ICacheManager, InMemoryCacheManager>(); // Default cache manager
            services.AddSingleton<ICacheKeyGenerator, DefaultCacheKeyGenerator>(); // Default key generator
            services.AddSingleton<ICacheMetricsProvider, ConsoleCacheMetricsProvider>();

            return services;
        }

        /// <summary>
        /// Adds MethodCache services and automatically registers all services with cache attributes from the specified assemblies.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="configure">Optional configuration action</param>
        /// <param name="assemblies">Assemblies to scan for services with cache attributes. If null, scans the calling assembly.</param>
        /// <returns>The service collection for chaining</returns>
        public static IServiceCollection AddMethodCache(this IServiceCollection services, Action<IMethodCacheConfiguration>? configure = null, params Assembly[]? assemblies)
        {
            // Add core MethodCache services
            services.AddMethodCache(configure);

            // Auto-register services with cache attributes
            services.AddMethodCacheServices(assemblies);

            return services;
        }

        /// <summary>
        /// Adds MethodCache services and automatically registers all services with cache attributes using the specified options.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="configure">Optional configuration action</param>
        /// <param name="options">Options for automatic service registration</param>
        /// <returns>The service collection for chaining</returns>
        public static IServiceCollection AddMethodCache(this IServiceCollection services, Action<IMethodCacheConfiguration>? configure, MethodCacheRegistrationOptions options)
        {
            // Add core MethodCache services
            services.AddMethodCache(configure);

            // Auto-register services with cache attributes using options
            services.AddMethodCacheServices(options);

            return services;
        }

        /// <summary>
        /// Automatically registers services with cache attributes from the specified assemblies.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="assemblies">Assemblies to scan. If null, scans the calling assembly.</param>
        /// <returns>The service collection for chaining</returns>
        public static IServiceCollection AddMethodCacheServices(this IServiceCollection services, params Assembly[]? assemblies)
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
            var interfacesWithCacheAttributes = FindInterfacesWithCacheAttributes(assembliesToScan, options);

            foreach (var interfaceType in interfacesWithCacheAttributes)
            {
                RegisterServiceWithCaching(services, interfaceType, options);
            }

            return services;
        }

        private static Assembly[] GetAssembliesToScan(MethodCacheRegistrationOptions options)
        {
            var assemblies = options.Assemblies?.ToList() ?? new List<Assembly> { Assembly.GetCallingAssembly() };

            if (options.ScanReferencedAssemblies)
            {
                var referencedAssemblies = assemblies
                    .SelectMany(a => a.GetReferencedAssemblies())
                    .Select(Assembly.Load)
                    .Where(a => !assemblies.Contains(a))
                    .ToList();

                assemblies.AddRange(referencedAssemblies);
            }

            return assemblies.ToArray();
        }

        private static List<Type> FindInterfacesWithCacheAttributes(Assembly[] assemblies, MethodCacheRegistrationOptions options)
        {
            var interfaces = new List<Type>();

            foreach (var assembly in assemblies)
            {
                try
                {
                    var assemblyInterfaces = assembly.GetTypes()
                        .Where(t => t.IsInterface)
                        .Where(t => HasCacheAttributes(t))
                        .Where(t => options.InterfaceFilter?.Invoke(t) ?? true)
                        .ToList();

                    interfaces.AddRange(assemblyInterfaces);
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // Handle cases where some types in the assembly can't be loaded
                    var loadableTypes = ex.Types.Where(t => t != null).Cast<Type>();
                    var assemblyInterfaces = loadableTypes
                        .Where(t => t.IsInterface)
                        .Where(t => HasCacheAttributes(t))
                        .Where(t => options.InterfaceFilter?.Invoke(t) ?? true)
                        .ToList();

                    interfaces.AddRange(assemblyInterfaces);
                }
            }

            return interfaces;
        }

        private static bool HasCacheAttributes(Type interfaceType)
        {
            return interfaceType.GetMethods()
                .Any(m => m.GetCustomAttributes(typeof(CacheAttribute), false).Any() ||
                         m.GetCustomAttributes(typeof(CacheInvalidateAttribute), false).Any());
        }

        private static void RegisterServiceWithCaching(IServiceCollection services, Type interfaceType, MethodCacheRegistrationOptions options)
        {
            // Find concrete implementation
            var implementationType = FindImplementationType(interfaceType, options);

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

        private static Type? FindImplementationType(Type interfaceType, MethodCacheRegistrationOptions options)
        {
            var assembliesToScan = GetAssembliesToScan(options);

            foreach (var assembly in assembliesToScan)
            {
                try
                {
                    var implementations = assembly.GetTypes()
                        .Where(t => !t.IsInterface && !t.IsAbstract)
                        .Where(t => interfaceType.IsAssignableFrom(t))
                        .Where(t => options.ImplementationFilter?.Invoke(t) ?? true)
                        .ToList();

                    if (implementations.Count == 1)
                    {
                        return implementations.First();
                    }

                    if (implementations.Count > 1)
                    {
                        // Prefer implementations with the same name as the interface (minus the 'I' prefix)
                        var preferredName = interfaceType.Name.StartsWith("I") ? interfaceType.Name.Substring(1) : interfaceType.Name;
                        var preferred = implementations.FirstOrDefault(t => t.Name == preferredName);
                        if (preferred != null)
                        {
                            return preferred;
                        }

                        // Return the first one if no preferred match
                        return implementations.First();
                    }
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // Handle cases where some types in the assembly can't be loaded
                    var loadableTypes = ex.Types.Where(t => t != null).Cast<Type>();
                    var implementations = loadableTypes
                        .Where(t => !t.IsInterface && !t.IsAbstract)
                        .Where(t => interfaceType.IsAssignableFrom(t))
                        .Where(t => options.ImplementationFilter?.Invoke(t) ?? true)
                        .ToList();

                    if (implementations.Any())
                    {
                        return implementations.First();
                    }
                }
            }

            return null;
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
                    .SelectMany(a => a.GetTypes())
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