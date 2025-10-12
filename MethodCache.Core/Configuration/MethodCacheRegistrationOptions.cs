using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace MethodCache.Core
{
    /// <summary>
    /// Options for configuring automatic service registration with MethodCache.
    /// </summary>
    public class MethodCacheRegistrationOptions
    {
        /// <summary>
        /// Assemblies to scan for services with cache attributes. If null or empty, scans the calling assembly.
        /// </summary>
        public Assembly[]? Assemblies { get; set; }

        /// <summary>
        /// Default service lifetime for registered services. Default is Singleton.
        /// </summary>
        public ServiceLifetime DefaultServiceLifetime { get; set; } = ServiceLifetime.Singleton;

        /// <summary>
        /// Whether to automatically register concrete implementations. Default is true.
        /// </summary>
        public bool RegisterConcreteImplementations { get; set; } = true;

        /// <summary>
        /// Whether to throw an exception if a concrete implementation cannot be found for an interface with cache attributes.
        /// Default is false (will log a warning instead).
        /// </summary>
        public bool ThrowOnMissingImplementation { get; set; } = false;

        /// <summary>
        /// Predicate to filter which interfaces should be auto-registered. If null, all interfaces with cache attributes are registered.
        /// </summary>
        public Func<Type, bool>? InterfaceFilter { get; set; }

        /// <summary>
        /// Predicate to filter which concrete implementations should be auto-registered. If null, all implementations are considered.
        /// </summary>
        public Func<Type, bool>? ImplementationFilter { get; set; }

        /// <summary>
        /// Custom service lifetime resolver. If provided, overrides DefaultServiceLifetime for specific types.
        /// </summary>
        public Func<Type, ServiceLifetime>? ServiceLifetimeResolver { get; set; }

        /// <summary>
        /// Whether to scan for interfaces in referenced assemblies. Default is false.
        /// </summary>
        public bool ScanReferencedAssemblies { get; set; } = false;

        /// <summary>
        /// Creates default registration options that scan the calling assembly.
        /// </summary>
        /// <returns>Default registration options</returns>
        public static MethodCacheRegistrationOptions Default()
        {
            return new MethodCacheRegistrationOptions
            {
                Assemblies = new[] { Assembly.GetCallingAssembly() }
            };
        }

        /// <summary>
        /// Creates registration options that scan the specified assemblies.
        /// </summary>
        /// <param name="assemblies">Assemblies to scan</param>
        /// <returns>Registration options for the specified assemblies</returns>
        public static MethodCacheRegistrationOptions ForAssemblies(params Assembly[] assemblies)
        {
            return new MethodCacheRegistrationOptions
            {
                Assemblies = assemblies
            };
        }

        /// <summary>
        /// Creates registration options that scan the assembly containing the specified type.
        /// </summary>
        /// <typeparam name="T">Type whose assembly should be scanned</typeparam>
        /// <returns>Registration options for the assembly containing the specified type</returns>
        public static MethodCacheRegistrationOptions ForAssemblyContaining<T>()
        {
            return new MethodCacheRegistrationOptions
            {
                Assemblies = new[] { typeof(T).Assembly }
            };
        }
    }
}
