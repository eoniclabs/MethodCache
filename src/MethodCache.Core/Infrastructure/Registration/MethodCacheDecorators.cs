using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Infrastructure
{
    /// <summary>
    /// The generated decorators of every loaded assembly. The source generator registers each assembly's
    /// decorators from a module initializer, so <c>AddMethodCache()</c> finds them without scanning types,
    /// which keeps registration working under trimming and Native AOT.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class MethodCacheDecorators
    {
        private static readonly ConcurrentDictionary<Type, DecoratorRegistration> Registrations = new();

        // Whether each loaded assembly references MethodCache, so it is checked once
        private static readonly ConcurrentDictionary<Assembly, bool> UsesMethodCache = new();

        // The loaded assemblies that reference MethodCache, as of the last scan
        private static Assembly[] _methodCacheAssemblies = [];

        // Counts assembly loads, so the loaded assemblies are scanned again only after one
        private static int _loadVersion;
        private static int _scannedVersion = -1;
        private static readonly object ScanLock = new();

        private static readonly string CoreAssemblyName = typeof(MethodCacheDecorators).Assembly.GetName().Name!;

        static MethodCacheDecorators()
        {
            if (RuntimeFeature.IsDynamicCodeSupported)
            {
                AppDomain.CurrentDomain.AssemblyLoad += static (_, _) => Interlocked.Increment(ref _loadVersion);
            }
        }

        /// <summary>
        /// Registers a generated decorator. Called by generated code; a later registration for the same
        /// service type replaces the earlier one.
        /// </summary>
        public static void Register(DecoratorRegistration registration)
        {
            ArgumentNullException.ThrowIfNull(registration);
            Registrations[registration.ServiceType] = registration;
        }

        /// <summary>
        /// Returns the generated decorator for <paramref name="serviceType"/>, running the module initializer
        /// of the assembly that declares it first, so its decorators are registered even if nothing in that
        /// assembly has run yet. The decorator of another assembly's interface, from
        /// <c>[assembly: CacheInterface]</c>, lives in the assembly with the attribute, so on a miss the module
        /// initializers of the loaded assemblies that use MethodCache run too.
        /// </summary>
        public static bool TryGet(Type serviceType, [NotNullWhen(true)] out DecoratorRegistration? registration)
        {
            ArgumentNullException.ThrowIfNull(serviceType);

            if (Registrations.TryGetValue(serviceType, out registration))
            {
                return true;
            }

            RuntimeHelpers.RunModuleConstructor(serviceType.Module.ModuleHandle);
            if (Registrations.TryGetValue(serviceType, out registration))
            {
                return true;
            }

            InitializeLoadedAssemblies();
            return Registrations.TryGetValue(serviceType, out registration);
        }

        /// <summary>
        /// Runs the module initializer of each loaded assembly that references MethodCache. An assembly is loaded once
        /// code that uses one of its types is compiled, so a library the startup code calls into is covered even if
        /// none of its code has run yet. Running a module initializer again does nothing, and waits while another
        /// thread runs it, so a parallel lookup never misses a decorator that is being registered. The loaded
        /// assemblies are listed again only after another one loads. Native AOT runs every module initializer at
        /// startup, so there is nothing to do there.
        /// </summary>
        private static void InitializeLoadedAssemblies()
        {
            if (!RuntimeFeature.IsDynamicCodeSupported)
            {
                return;
            }

            foreach (var assembly in MethodCacheAssemblies())
            {
                RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
            }
        }

        private static Assembly[] MethodCacheAssemblies()
        {
            // Read before listing: an assembly that loads during the scan bumps the version, so the next miss rescans
            var version = Volatile.Read(ref _loadVersion);
            if (version == Volatile.Read(ref _scannedVersion))
            {
                return Volatile.Read(ref _methodCacheAssemblies);
            }

            lock (ScanLock)
            {
                if (version != _scannedVersion)
                {
                    // Only assemblies that use MethodCache can hold decorators; other libraries' initializers stay untouched
                    var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                        .Where(a => !a.IsDynamic && UsesMethodCache.GetOrAdd(a, static a => a.GetReferencedAssemblies().Any(name => name.Name == CoreAssemblyName)))
                        .ToArray();
                    Volatile.Write(ref _methodCacheAssemblies, assemblies);
                    Volatile.Write(ref _scannedVersion, version);
                }

                return _methodCacheAssemblies;
            }
        }

        /// <summary>
        /// Returns the generated decorators declared in <paramref name="assembly"/>.
        /// </summary>
        public static IReadOnlyList<DecoratorRegistration> For(Assembly assembly)
        {
            ArgumentNullException.ThrowIfNull(assembly);

            RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
            return Registrations.Values
                .Where(r => r.ServiceType.Assembly == assembly)
                .OrderBy(r => r.ServiceType.FullName, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Returns every registered decorator. Under Native AOT all module initializers run at startup, so this
        /// covers the whole application.
        /// </summary>
        public static IReadOnlyList<DecoratorRegistration> All()
            => Registrations.Values.OrderBy(r => r.ServiceType.FullName, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// How to wrap one service interface in its generated caching decorator.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class DecoratorRegistration
    {
        /// <param name="serviceType">The interface with cache attributes.</param>
        /// <param name="decorate">Creates the decorator around the inner service.</param>
        /// <param name="addPolicies">Registers the policies generated from the interface's attributes.</param>
        /// <param name="implementationType">
        /// The implementation found next to the interface at compile time, registered when the application has
        /// not registered the interface itself; null when there is none.
        /// </param>
        public DecoratorRegistration(
            Type serviceType,
            Func<IServiceProvider, object, object> decorate,
            Action<IServiceCollection>? addPolicies = null,
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type? implementationType = null)
        {
            ServiceType = serviceType ?? throw new ArgumentNullException(nameof(serviceType));
            Decorate = decorate ?? throw new ArgumentNullException(nameof(decorate));
            AddPolicies = addPolicies;
            ImplementationType = implementationType;
        }

        /// <summary>
        /// The interface with cache attributes.
        /// </summary>
        public Type ServiceType { get; }

        /// <summary>
        /// Creates the decorator around the inner service.
        /// </summary>
        public Func<IServiceProvider, object, object> Decorate { get; }

        /// <summary>
        /// Registers the policies generated from the interface's attributes.
        /// </summary>
        public Action<IServiceCollection>? AddPolicies { get; }

        /// <summary>
        /// The implementation found next to the interface at compile time, if any.
        /// </summary>
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        public Type? ImplementationType { get; }
    }
}
