using System;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MethodCache.Core.Configuration.Sources;
using MethodCache.Core.Configuration.RuntimeConfiguration;

namespace MethodCache.Core.Configuration
{
    /// <summary>
    /// Extension methods for configuring MethodCache with multiple sources
    /// </summary>
    public static class ConfigurationExtensions
    {
        /// <summary>
        /// Adds JSON configuration support
        /// </summary>
        public static IServiceCollection AddJsonConfiguration(
            this IServiceCollection services,
            IConfiguration configuration,
            string sectionName = "MethodCache")
        {
            services.AddSingleton<Sources.IConfigurationSource>(provider =>
                new JsonConfigurationSource(configuration, sectionName));
            
            return services;
        }
        
        /// <summary>
        /// Adds YAML configuration support
        /// </summary>
        public static IServiceCollection AddYamlConfiguration(
            this IServiceCollection services,
            string yamlFilePath)
        {
            if (!File.Exists(yamlFilePath))
            {
                throw new FileNotFoundException($"YAML configuration file not found: {yamlFilePath}");
            }
            
            services.AddSingleton<Sources.IConfigurationSource>(provider =>
                new YamlConfigurationSource(yamlFilePath));
            
            return services;
        }
        
        /// <summary>
        /// Adds runtime configuration support with IOptionsMonitor
        /// </summary>
        public static IServiceCollection AddRuntimeConfiguration(
            this IServiceCollection services,
            Action<MethodCacheOptions>? configure = null)
        {
            // Configure options
            var optionsBuilder = services.AddOptions<MethodCacheOptions>();
            
            if (configure != null)
            {
                optionsBuilder.Configure(configure);
            }
            
            // Bind to configuration
            optionsBuilder.BindConfiguration("MethodCache");
            
            // Add as configuration source
            services.AddSingleton<Sources.IConfigurationSource>(provider =>
            {
                var monitor = provider.GetRequiredService<IOptionsMonitor<MethodCacheOptions>>();
                var manager = provider.GetService<IMethodCacheConfigurationManager>();
                
                // Setup change callback to reload configuration
                return new OptionsMonitorConfigurationSource(monitor, options =>
                {
                    manager?.LoadConfigurationAsync().GetAwaiter().GetResult();
                });
            });
            
            return services;
        }
        
        /// <summary>
        /// Configures MethodCache with multiple configuration sources
        /// </summary>
        public static IServiceCollection AddMethodCacheWithSources(
            this IServiceCollection services,
            Action<MethodCacheBuilder>? configure = null)
        {
            var builder = new MethodCacheBuilder(services);
            
            // Add default attribute source
            builder.AddAttributeSource(Assembly.GetCallingAssembly());
            
            // Let user configure additional sources
            configure?.Invoke(builder);
            
            // Register configuration manager
            services.AddSingleton<IMethodCacheConfigurationManager>(provider =>
            {
                var manager = new ConfigurationManager(null);
                
                // Add all registered sources
                var sources = provider.GetServices<Sources.IConfigurationSource>();
                foreach (var source in sources)
                {
                    manager.AddSource(source);
                }
                
                // Load initial configuration
                manager.LoadConfigurationAsync().GetAwaiter().GetResult();
                
                return manager;
            });
            
            // Register core MethodCache services
            services.AddSingleton<ICacheManager, InMemoryCacheManager>();
            services.AddSingleton<ICacheKeyGenerator, DefaultCacheKeyGenerator>();
            services.AddSingleton<ICacheMetricsProvider, ConsoleCacheMetricsProvider>();
            
            // Register IMethodCacheConfiguration that uses the manager
            services.AddSingleton<IMethodCacheConfiguration>(provider =>
            {
                var manager = provider.GetRequiredService<IMethodCacheConfigurationManager>();
                return new ManagedMethodCacheConfiguration(manager);
            });
            
            return services;
        }
    }
    
    /// <summary>
    /// Builder for configuring MethodCache
    /// </summary>
    public class MethodCacheBuilder
    {
        private readonly IServiceCollection _services;
        
        public MethodCacheBuilder(IServiceCollection services)
        {
            _services = services;
        }
        
        /// <summary>
        /// Adds attribute-based configuration source
        /// </summary>
        public MethodCacheBuilder AddAttributeSource(params Assembly[] assemblies)
        {
            _services.AddSingleton<Sources.IConfigurationSource>(new AttributeConfigurationSource(assemblies));
            return this;
        }
        
        /// <summary>
        /// Adds JSON configuration source
        /// </summary>
        public MethodCacheBuilder AddJsonConfiguration(IConfiguration configuration, string sectionName = "MethodCache")
        {
            _services.AddJsonConfiguration(configuration, sectionName);
            return this;
        }
        
        /// <summary>
        /// Adds YAML configuration source
        /// </summary>
        public MethodCacheBuilder AddYamlConfiguration(string yamlFilePath)
        {
            _services.AddYamlConfiguration(yamlFilePath);
            return this;
        }
        
        /// <summary>
        /// Adds runtime configuration with IOptionsMonitor
        /// </summary>
        public MethodCacheBuilder AddRuntimeConfiguration(Action<MethodCacheOptions>? configure = null)
        {
            _services.AddRuntimeConfiguration(configure);
            return this;
        }
        
        /// <summary>
        /// Adds programmatic configuration
        /// </summary>
        public MethodCacheBuilder AddProgrammaticConfiguration(Action<IProgrammaticConfigurationBuilder> configure)
        {
            var source = new ProgrammaticConfigurationSource();
            var builder = new ProgrammaticConfigurationBuilder(source);
            configure(builder);
            _services.AddSingleton<Sources.IConfigurationSource>(source);
            return this;
        }
    }
    
    /// <summary>
    /// Builder for programmatic configuration
    /// </summary>
    public interface IProgrammaticConfigurationBuilder
    {
        IProgrammaticConfigurationBuilder AddMethod(string serviceType, string methodName, Action<CacheMethodSettings> configure);
    }
    
    internal class ProgrammaticConfigurationBuilder : IProgrammaticConfigurationBuilder
    {
        private readonly ProgrammaticConfigurationSource _source;
        
        public ProgrammaticConfigurationBuilder(ProgrammaticConfigurationSource source)
        {
            _source = source;
        }
        
        public IProgrammaticConfigurationBuilder AddMethod(string serviceType, string methodName, Action<CacheMethodSettings> configure)
        {
            var settings = new CacheMethodSettings();
            configure(settings);
            _source.AddMethodConfiguration(serviceType, methodName, settings);
            return this;
        }
    }
    
    /// <summary>
    /// Adapter that bridges IMethodCacheConfigurationManager to IMethodCacheConfiguration
    /// </summary>
    internal class ManagedMethodCacheConfiguration : MethodCacheConfiguration
    {
        private readonly IMethodCacheConfigurationManager _manager;
        
        public ManagedMethodCacheConfiguration(IMethodCacheConfigurationManager manager)
        {
            _manager = manager;
            
            // Load initial configuration
            var allConfigs = _manager.GetAllConfigurations();
            foreach (var config in allConfigs)
            {
                AddMethod(config.Key, config.Value);
            }
            
            // Subscribe to changes
            _manager.ConfigurationChanged += (sender, args) =>
            {
                // Reload configuration
                var updatedConfigs = _manager.GetAllConfigurations();
                
                // Clear and reload (in production, you'd want to be more sophisticated)
                ClearAllMethods();
                foreach (var config in updatedConfigs)
                {
                    AddMethod(config.Key, config.Value);
                }
            };
        }
    }
}