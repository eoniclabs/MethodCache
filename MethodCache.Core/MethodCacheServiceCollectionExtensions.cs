using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MethodCache.Core.Configuration;
using MethodCache.Core.Configuration.Fluent;
using MethodCache.Core.Configuration.Policies;
using MethodCache.Core.Configuration.Runtime;
using MethodCache.Core.Configuration.Sources;
using MethodCache.Core.Configuration.Resolver;
using MethodCache.Core.Runtime.Defaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            EnsureCoreServices(services);

            var configuration = BuildStartupConfiguration(configure);
            RegisterFluentPolicySource(services, configuration);

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

            var configuration = BuildStartupConfiguration(configure);
            RegisterFluentPolicySource(services, configuration);

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

            var configuration = BuildStartupConfiguration(configure);
            RegisterFluentPolicySource(services, configuration);

            return services;
        }

        private static MethodCacheConfiguration BuildStartupConfiguration(Action<IMethodCacheConfiguration>? configure)
        {
            var configuration = new MethodCacheConfiguration();
            configure?.Invoke(configuration);
            return configuration;
        }

        private static void EnsureCoreServices(IServiceCollection services)
        {
            services.TryAddSingleton<ICacheManager, InMemoryCacheManager>();
            services.TryAddSingleton<ICacheKeyGenerator, DefaultCacheKeyGenerator>();
            services.TryAddSingleton<ICacheMetricsProvider, ConsoleCacheMetricsProvider>();

            if (!services.Any(sd => sd.ServiceType == typeof(RuntimePolicyOverrideStore)))
            {
                services.AddSingleton<RuntimePolicyOverrideStore>();
            }

            if (!services.Any(sd => sd.ServiceType == typeof(RuntimeOverridePolicySource)))
            {
                services.AddSingleton<RuntimeOverridePolicySource>();
                services.AddSingleton<PolicySourceRegistration>(sp =>
                    new PolicySourceRegistration(
                        sp.GetRequiredService<RuntimeOverridePolicySource>(),
                        PolicySourcePriority.RuntimeOverrides));
            }

            services.TryAddSingleton<IRuntimeCacheConfigurator, RuntimeCacheConfigurator>();

            PolicyRegistrationExtensions.EnsurePolicyServices(services);
        }

        private static void RegisterFluentPolicySource(IServiceCollection services, MethodCacheConfiguration configuration)
        {
            services.AddSingleton<PolicySourceRegistration>(_ =>
                new PolicySourceRegistration(new FluentPolicySource(configuration), PolicySourcePriority.StartupFluent));
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

            return services.AddMethodCache(config => config.ApplyFluent(configure));
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

        public static IServiceCollection AddMethodCacheWithSources(this IServiceCollection services, Action<MethodCacheConfigurationBuilder>? configure = null, Action<IMethodCacheConfiguration>? configureFluent = null)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            EnsureCoreServices(services);

            var configuration = BuildStartupConfiguration(configureFluent);
            RegisterFluentPolicySource(services, configuration);

            var builder = new MethodCacheConfigurationBuilder(services);
            builder.AddAttributeSource(Assembly.GetCallingAssembly());

            configure?.Invoke(builder);

            builder.Apply();

            return services;
        }

        public interface IProgrammaticConfigurationBuilder
        {
            IProgrammaticConfigurationBuilder AddMethod(string serviceType, string methodName, Action<CacheMethodSettings> configure);
        }

        public sealed class MethodCacheConfigurationBuilder
        {
            private readonly IServiceCollection _services;
            private readonly List<Configuration.Sources.IConfigurationSource> _configurationSources = new();

            public MethodCacheConfigurationBuilder(IServiceCollection services)
            {
                _services = services ?? throw new ArgumentNullException(nameof(services));
            }

            public MethodCacheConfigurationBuilder AddAttributeSource(params Assembly[] assemblies)
            {
                var targets = assemblies is { Length: > 0 } ? assemblies : new[] { Assembly.GetCallingAssembly() };
                RegisterAttributePolicySource(_services, targets);
                _services.AddMethodCacheServices(MethodCacheRegistrationOptions.ForAssemblies(targets));
                return this;
            }

            public MethodCacheConfigurationBuilder AddJsonConfiguration(IConfiguration configuration, string sectionName = "MethodCache")
            {
                if (configuration == null)
                {
                    throw new ArgumentNullException(nameof(configuration));
                }

                _configurationSources.Add(new JsonConfigurationSource(configuration, sectionName));
                return this;
            }

            public MethodCacheConfigurationBuilder AddYamlConfiguration(string yamlFilePath)
            {
                if (string.IsNullOrWhiteSpace(yamlFilePath))
                {
                    throw new ArgumentException("YAML file path must be provided.", nameof(yamlFilePath));
                }

                _configurationSources.Add(new YamlConfigurationSource(yamlFilePath));
                return this;
            }

            public MethodCacheConfigurationBuilder AddProgrammaticConfiguration(Action<IProgrammaticConfigurationBuilder> configure)
            {
                if (configure == null)
                {
                    throw new ArgumentNullException(nameof(configure));
                }

                var source = new ProgrammaticConfigurationSource();
                var builder = new ProgrammaticConfigurationBuilder(source);
                configure(builder);
                _configurationSources.Add(source);
                return this;
            }

            public MethodCacheConfigurationBuilder AddRuntimeConfiguration(Action<MethodCacheOptions>? configure = null)
            {
                var optionsBuilder = _services.AddOptions<MethodCacheOptions>();
                optionsBuilder.BindConfiguration("MethodCache");

                if (configure != null)
                {
                    optionsBuilder.Configure(configure);
                }

                _services.AddSingleton<PolicySourceRegistration>(sp =>
                    new PolicySourceRegistration(
                        new OptionsMonitorPolicySource(sp.GetRequiredService<IOptionsMonitor<MethodCacheOptions>>()),
                        PolicySourcePriority.ConfigurationFiles + 5));

                return this;
            }

            internal void Apply()
            {
                if (_configurationSources.Count == 0)
                {
                    return;
                }

                var sources = _configurationSources.ToArray();
                _services.AddSingleton<PolicySourceRegistration>(_ =>
                    new PolicySourceRegistration(new ConfigFilePolicySource(sources), PolicySourcePriority.ConfigurationFiles));
            }

            private sealed class ProgrammaticConfigurationBuilder : IProgrammaticConfigurationBuilder
            {
                private readonly ProgrammaticConfigurationSource _source;

                public ProgrammaticConfigurationBuilder(ProgrammaticConfigurationSource source)
                {
                    _source = source ?? throw new ArgumentNullException(nameof(source));
                }

                public IProgrammaticConfigurationBuilder AddMethod(string serviceType, string methodName, Action<CacheMethodSettings> configure)
                {
                    if (string.IsNullOrWhiteSpace(serviceType))
                    {
                        throw new ArgumentException("Service type must be provided.", nameof(serviceType));
                    }

                    if (string.IsNullOrWhiteSpace(methodName))
                    {
                        throw new ArgumentException("Method name must be provided.", nameof(methodName));
                    }

                    if (configure == null)
                    {
                        throw new ArgumentNullException(nameof(configure));
                    }

                    var settings = new CacheMethodSettings();
                    configure(settings);
                    _source.AddMethodConfiguration(serviceType, methodName, settings);
                    return this;
                }
            }
        }

    }
}
